using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MistyStep.Server.Tests.Support;
using static MistyStep.Server.Tests.Support.TestApp;

namespace MistyStep.Server.Tests;

/// <summary>Connection filter, Host check, headers, request bodies, lockout and rate limits.</summary>
public sealed class PipelineTests : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await StartAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    // --- Connection filter ---

    [Fact]
    public async Task Public_address_is_aborted_without_a_response()
    {
        var (_, token) = _app.Pair();
        await Assert.ThrowsAnyAsync<Exception>(() => _app.CreateClient(token, PublicAddress).GetAsync("/v1/ping", Ct));
    }

    [Fact]
    public async Task Lan_address_is_aborted_until_home_wifi_mode_is_on()
    {
        var (_, token) = _app.Pair();
        await Assert.ThrowsAnyAsync<Exception>(() => _app.CreateClient(token, LanAddress).GetAsync("/v1/ping", Ct));

        _app.State.LanEnabled = true;
        using var response = await _app.CreateClient(token, LanAddress).GetAsync("/v1/ping", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Tailscale_address_is_aborted_when_tailscale_mode_is_off()
    {
        var (_, token) = _app.Pair();
        _app.State.TailscaleEnabled = false;
        await Assert.ThrowsAnyAsync<Exception>(() => _app.CreateClient(token).GetAsync("/v1/ping", Ct));
    }

    [Fact]
    public async Task Loopback_is_always_allowed()
    {
        var (_, token) = _app.Pair();
        _app.State.TailscaleEnabled = false;
        using var response = await _app.CreateClient(token, IPAddress.Loopback).GetAsync("/v1/ping", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Host header ---

    [Theory]
    [InlineData("evil.example.com:45654")]
    [InlineData("100.101.102.103")]
    [InlineData("100.101.102.103:80")]
    [InlineData("my-laptop:45654")]
    public async Task Foreign_host_is_not_found(string host)
    {
        var (_, token) = _app.Pair();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
        request.Headers.Host = host;
        using var response = await _app.CreateClient(token).SendAsync(request, Ct);
        await RouteTests.AssertEmptyNotFound(response);
    }

    [Theory]
    [InlineData("my-laptop.tailnet-name.ts.net:45654")]
    [InlineData("my-laptop.local:45654")]
    [InlineData("localhost:45654")]
    public async Task Known_hosts_are_accepted(string host)
    {
        var (_, token) = _app.Pair();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
        request.Headers.Host = host;
        using var response = await _app.CreateClient(token).SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Headers on every response ---

    [Fact]
    public async Task Every_response_has_no_store_nosniff_and_no_server_header()
    {
        var (_, token) = _app.Pair();
        var code = _app.Registry.CreatePairingCode($"http://{TailscaleHost}", PcName);
        _app.SetText("x");

        var authed = _app.CreateClient(token);
        var anonymous = _app.CreateClient();
        var responses = new List<HttpResponseMessage>
        {
            await authed.GetAsync("/v1/meta", Ct),          // 200 JSON
            await authed.GetAsync("/v1/data", Ct),          // 204
            await authed.GetAsync("/v1/ping", Ct),          // 200
            await anonymous.GetAsync("/v1/meta", Ct),       // 401
            await authed.GetAsync("/nope", Ct),             // 404
            await anonymous.GetAsync($"/pair?c={code.Code}", Ct), // 200 HTML
            await anonymous.GetAsync("/pair?c=bad", Ct),    // 410 HTML
        };

        foreach (var response in responses)
        {
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.False(response.Headers.Contains("Server"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Set-Cookie"));
            response.Dispose();
        }
    }

    [Fact]
    public void Kestrel_limits_match_the_spec()
    {
        var options = new KestrelServerOptions();
        MistyStepServer.ConfigureLimits(options);

        Assert.False(options.AddServerHeader);
        Assert.Equal(0, options.Limits.MaxRequestBodySize);
        Assert.Equal(8 * 1024, options.Limits.MaxRequestHeadersTotalSize);
        Assert.Equal(2 * 1024, options.Limits.MaxRequestLineSize);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Limits.RequestHeadersTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Limits.KeepAliveTimeout);
        Assert.Equal(16, options.Limits.MaxConcurrentConnections);
    }

    // --- Request bodies ---

    [Fact]
    public async Task Get_with_a_body_is_rejected()
    {
        var (_, token) = _app.Pair();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/ping") { Content = new StringContent("hello") };
        using var response = await _app.CreateClient(token).SendAsync(request, Ct);
        await RouteTests.AssertEmptyNotFound(response);
    }

    [Fact]
    public async Task Post_with_a_body_is_rejected()
    {
        var (_, token) = _app.Pair();
        using var response = await _app.CreateClient(token).PostAsync("/v1/meta", new StringContent("{}"), Ct);
        await RouteTests.AssertEmptyNotFound(response);
    }

    // --- Abuse limits ---

    [Fact]
    public async Task Five_failed_authentications_lock_the_address_out()
    {
        var (_, token) = _app.Pair();
        var bad = _app.CreateClient("wrong-token");
        for (var i = 0; i < 5; i++)
        {
            using var response = await bad.GetAsync("/v1/meta", Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // Even the right token is refused from that address now.
        using (var locked = await _app.CreateClient(token).GetAsync("/v1/meta", Ct))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
            var json = await ReadJsonAsync(locked);
            Assert.Equal("rate_limited", json.GetProperty("error").GetString());
            Assert.Equal(1, json.GetProperty("v").GetInt32());
        }

        // Another address is unaffected.
        var (_, otherToken) = _app.Pair(OtherTailnetAddress);
        using (var other = await _app.CreateClient(otherToken, OtherTailnetAddress).GetAsync("/v1/meta", Ct))
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        // The lockout ends after five minutes.
        _app.Clock.Advance(5 * 60_000);
        using (var released = await _app.CreateClient(token).GetAsync("/v1/meta", Ct))
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);
    }

    [Fact]
    public async Task Locked_out_address_gets_an_html_429_on_the_pairing_page()
    {
        var bad = _app.CreateClient("wrong-token");
        for (var i = 0; i < 5; i++) (await bad.GetAsync("/v1/ping", Ct)).Dispose();

        var code = _app.Registry.CreatePairingCode($"http://{TailscaleHost}", PcName);
        using var response = await _app.CreateClient().GetAsync($"/pair?c={code.Code}", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, _app.Registry.PendingCount); // the code was not redeemed
    }

    [Fact]
    public async Task More_than_twenty_requests_per_second_per_device_are_refused()
    {
        var (_, token) = _app.Pair();
        var client = _app.CreateClient(token);
        for (var i = 0; i < 20; i++)
        {
            using var ok = await client.GetAsync("/v1/ping", Ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using (var limited = await client.GetAsync("/v1/ping", Ct))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal("rate_limited", (await ReadJsonAsync(limited)).GetProperty("error").GetString());
        }

        _app.Clock.Advance(1000);
        using var later = await client.GetAsync("/v1/ping", Ct);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }
}
