using System.Net;
using MistyStep.Server.Tests.Support;
using static MistyStep.Server.Tests.Support.TestApp;

namespace MistyStep.Server.Tests;

/// <summary>Every route: success, missing token, wrong token, wrong method, unknown path.</summary>
public sealed class RouteTests : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await StartAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    public static TheoryData<string> ApiPaths => new("/v1/meta", "/v1/data", "/v1/ping");

    [Fact]
    public async Task Ping_succeeds_with_a_token()
    {
        var (_, token) = _app.Pair();
        using var response = await _app.CreateClient(token).GetAsync("/v1/ping", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(1, json.GetProperty("v").GetInt32());
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(PcName, json.GetProperty("pc").GetString());
        Assert.Equal(AppVersion, json.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Meta_succeeds_with_a_token()
    {
        var (_, token) = _app.Pair();
        using var response = await _app.CreateClient(token).GetAsync("/v1/meta", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("none", (await ReadJsonAsync(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Data_succeeds_with_a_token()
    {
        var (_, token) = _app.Pair();
        _app.SetImage([1, 2, 3]);
        using var response = await _app.CreateClient(token).GetAsync("/v1/data", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Pair_succeeds_with_a_code()
    {
        var code = _app.Registry.CreatePairingCode($"http://{TailscaleHost}", PcName);
        using var response = await _app.CreateClient().GetAsync($"/pair?c={code.Code}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ApiPaths))]
    public async Task Missing_token_is_unauthorized(string path)
    {
        _app.Pair();
        using var response = await _app.CreateClient().GetAsync(path, Ct);
        await AssertUnauthorized(response);
    }

    [Theory]
    [MemberData(nameof(ApiPaths))]
    public async Task Wrong_token_is_unauthorized(string path)
    {
        _app.Pair();
        using var response = await _app.CreateClient("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA").GetAsync(path, Ct);
        await AssertUnauthorized(response);
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Token abc")]
    public async Task Malformed_authorization_is_unauthorized(string header)
    {
        _app.Pair();
        using var client = _app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
        request.Headers.TryAddWithoutValidation("Authorization", header);
        using var response = await client.SendAsync(request, Ct);
        await AssertUnauthorized(response);
    }

    [Fact]
    public async Task Bearer_scheme_is_case_insensitive()
    {
        var (_, token) = _app.Pair();
        using var client = _app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
        request.Headers.TryAddWithoutValidation("Authorization", "bearer " + token);
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Pair_without_a_valid_code_is_gone()
    {
        using var client = _app.CreateClient();
        using var missing = await client.GetAsync("/pair", Ct);
        using var unknown = await client.GetAsync("/pair?c=0123456789abcdef0123456789abcdef", Ct);
        Assert.Equal(HttpStatusCode.Gone, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, unknown.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/v1/meta")]
    [InlineData("PUT", "/v1/data")]
    [InlineData("DELETE", "/v1/ping")]
    [InlineData("HEAD", "/v1/meta")]
    [InlineData("OPTIONS", "/v1/meta")]
    [InlineData("POST", "/pair")]
    public async Task Wrong_method_is_not_found_with_empty_body(string method, string path)
    {
        var (_, token) = _app.Pair();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await _app.CreateClient(token).SendAsync(request, Ct);
        await AssertEmptyNotFound(response);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/v1")]
    [InlineData("/v1/")]
    [InlineData("/v1/meta/")]
    [InlineData("/V1/META")]
    [InlineData("/v2/meta")]
    [InlineData("/favicon.ico")]
    [InlineData("/pair/extra")]
    public async Task Unknown_path_is_not_found_with_empty_body(string path)
    {
        var (_, token) = _app.Pair();
        using var response = await _app.CreateClient(token).GetAsync(path, Ct);
        await AssertEmptyNotFound(response);
    }

    [Fact]
    public async Task Unknown_path_without_token_is_also_not_found()
    {
        using var response = await _app.CreateClient().GetAsync("/admin", Ct);
        await AssertEmptyNotFound(response);
    }

    internal static async Task AssertUnauthorized(HttpResponseMessage response)
    {
        // Errors are sent with 200 because the Shortcut does not receive the body of a 401.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(1, json.GetProperty("v").GetInt32());
        Assert.Equal("unauthorized", json.GetProperty("error").GetString());
        Assert.Equal("This device is not paired. Pair again from Misty Step on your PC.", json.GetProperty("message").GetString());
    }

    internal static async Task AssertEmptyNotFound(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }
}
