using System.Net;
using System.Text.RegularExpressions;
using MistyStep.Core.Pairing;
using MistyStep.Server.Tests.Support;
using static MistyStep.Server.Tests.Support.TestApp;

namespace MistyStep.Server.Tests;

public sealed partial class PairingPageTests : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await StartAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [GeneratedRegex("""<textarea id="code"[^>]*>(?<code>[^<]+)</textarea>""")]
    private static partial Regex SetupCodePattern();

    private string NewCode() => _app.Registry.CreatePairingCode($"http://{TailscaleHost}", PcName).Code;

    private static async Task<string> ReadSetupCodeAsync(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync(Ct);
        var match = SetupCodePattern().Match(html);
        Assert.True(match.Success, "setup code textarea not found");
        return WebUtility.HtmlDecode(match.Groups["code"].Value);
    }

    [Fact]
    public async Task Page_shows_a_working_setup_code()
    {
        using var response = await _app.CreateClient().GetAsync($"/pair?c={NewCode()}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var setupCode = await ReadSetupCodeAsync(response);
        Assert.True(SetupCode.TryDecode(setupCode, out var info));
        Assert.Equal($"http://{TailscaleHost}", info!.Url);
        Assert.Equal(PcName, info.PcName);

        // The token from the page authenticates.
        using var ping = await _app.CreateClient(info.Token).GetAsync("/v1/ping", Ct);
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
    }

    [Fact]
    public async Task Page_sends_csp_and_referrer_policy_and_has_no_external_resources()
    {
        using var response = await _app.CreateClient().GetAsync($"/pair?c={NewCode()}", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));

        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prefers-color-scheme: dark", html, StringComparison.Ordinal);
        Assert.Contains("document.execCommand('copy')", html, StringComparison.Ordinal);
        Assert.Contains("readonly", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_address_can_reload_and_sees_the_same_code()
    {
        var code = NewCode();
        var client = _app.CreateClient();
        using var first = await client.GetAsync($"/pair?c={code}", Ct);
        _app.Clock.Advance(2 * 60_000);
        using var reload = await client.GetAsync($"/pair?c={code}", Ct);

        Assert.Equal(HttpStatusCode.OK, reload.StatusCode);
        Assert.Equal(await ReadSetupCodeAsync(first), await ReadSetupCodeAsync(reload));
        Assert.Single(_app.Registry.Devices);
    }

    [Fact]
    public async Task Other_address_gets_410_after_the_code_is_used()
    {
        var code = NewCode();
        using var first = await _app.CreateClient().GetAsync($"/pair?c={code}", Ct);
        using var other = await _app.CreateClient(remote: OtherTailnetAddress).GetAsync($"/pair?c={code}", Ct);

        Assert.Equal(HttpStatusCode.Gone, other.StatusCode);
        Assert.Equal("text/html", other.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no longer works", await other.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Single(other.Headers.GetValues("Content-Security-Policy"));
    }

    [Fact]
    public async Task Expired_code_gets_410()
    {
        var code = NewCode();
        _app.Clock.Advance(10 * 60_000);
        using var response = await _app.CreateClient().GetAsync($"/pair?c={code}", Ct);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Empty(_app.Registry.Devices);
    }

    [Fact]
    public async Task Device_is_named_from_the_user_agent()
    {
        using var client = _app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/pair?c={NewCode()}");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)");
        using var response = await client.SendAsync(request, Ct);
        Assert.StartsWith("iPhone", _app.Registry.Devices.Single().Name);
        Assert.Contains("iPhone (Nov 14)", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pc_name_is_html_encoded()
    {
        _app.State.PcName = "<script>alert(1)</script>";
        var code = _app.Registry.CreatePairingCode($"http://{TailscaleHost}", "<b>pc</b>").Code;
        using var response = await _app.CreateClient().GetAsync($"/pair?c={code}", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_link_the_page_carries_the_build_steps()
    {
        using var response = await _app.CreateClient().GetAsync($"/pair?c={NewCode()}", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("Build steps", html, StringComparison.Ordinal);
        Assert.Contains("Get Contents of URL", html, StringComparison.Ordinal);
        Assert.Contains("X-MistyStep-Shortcut", html, StringComparison.Ordinal);
        Assert.Contains("Run it once", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Get the Shortcut</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_link_the_page_offers_one_tap_install_instead_of_build_steps()
    {
        _app.State.ShortcutUrl = "https://www.icloud.com/shortcuts/abc123";
        using var response = await _app.CreateClient().GetAsync($"/pair?c={NewCode()}", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("Get the Shortcut</a>", html, StringComparison.Ordinal);
        Assert.Contains("Paste the code", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Build steps", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shortcut_link_is_shown_only_when_it_is_https()
    {
        _app.State.ShortcutUrl = "https://www.icloud.com/shortcuts/abc123";
        using (var withLink = await _app.CreateClient().GetAsync($"/pair?c={NewCode()}", Ct))
        {
            Assert.Contains("href=\"https://www.icloud.com/shortcuts/abc123\"", await withLink.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        _app.State.ShortcutUrl = "javascript:alert(1)";
        using (var unsafeLink = await _app.CreateClient(remote: OtherTailnetAddress).GetAsync($"/pair?c={NewCode()}", Ct))
        {
            var html = await unsafeLink.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
            Assert.Contains("Paste from PC", html, StringComparison.Ordinal);
        }
    }
}
