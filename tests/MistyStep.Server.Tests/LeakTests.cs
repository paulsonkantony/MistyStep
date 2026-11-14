using System.Net;
using System.Text.RegularExpressions;
using MistyStep.Core.Pairing;
using MistyStep.Server.Tests.Support;
using static MistyStep.Server.Tests.Support.TestApp;

namespace MistyStep.Server.Tests;

/// <summary>Clipboard content, tokens, pairing codes and setup codes never reach a log line, at any level.</summary>
public sealed partial class LeakTests : IAsyncLifetime
{
    private const string Sentinel = "SENTINEL-7f3a9c-do-not-log";

    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await StartAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [GeneratedRegex("""<textarea id="code"[^>]*>(?<code>[^<]+)</textarea>""")]
    private static partial Regex SetupCodePattern();

    [Fact]
    public async Task Sentinel_token_and_codes_appear_in_no_log_line()
    {
        // Pair through the real pairing page.
        var pairingCode = _app.Registry.CreatePairingCode($"http://{TailscaleHost}", PcName).Code;
        using var page = await _app.CreateClient().GetAsync($"/pair?c={pairingCode}", Ct);
        var setupCode = WebUtility.HtmlDecode(SetupCodePattern().Match(await page.Content.ReadAsStringAsync(Ct)).Groups["code"].Value);
        Assert.True(SetupCode.TryDecode(setupCode, out var info));
        var token = info!.Token;
        var client = _app.CreateClient(token);

        // Text through every path.
        _app.SetText(Sentinel);
        (await client.GetAsync("/v1/meta", Ct)).Dispose();
        (await client.GetAsync("/v1/meta?new=1", Ct)).Dispose();
        (await client.GetAsync("/v1/data", Ct)).Dispose();

        // An image whose bytes contain the sentinel.
        _app.SetImage(System.Text.Encoding.UTF8.GetBytes(Sentinel));
        (await client.GetAsync("/v1/meta", Ct)).Dispose();
        (await client.GetAsync($"/v1/data?id={Sentinel}", Ct)).Dispose();

        // Failures: wrong token, reused pairing code from another address, odd headers.
        (await _app.CreateClient(Sentinel).GetAsync("/v1/meta", Ct)).Dispose();
        (await _app.CreateClient(remote: OtherTailnetAddress).GetAsync($"/pair?c={pairingCode}", Ct)).Dispose();
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/v1/ping"))
        {
            request.Headers.TryAddWithoutValidation("X-MistyStep-Shortcut", Sentinel);
            (await client.SendAsync(request, Ct)).Dispose();
        }

        (await client.GetAsync($"/{Sentinel}", Ct)).Dispose();

        var lines = _app.Logs.Lines;
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.DoesNotContain(Sentinel, line, StringComparison.Ordinal);
            Assert.DoesNotContain(token, line, StringComparison.Ordinal);
            Assert.DoesNotContain(pairingCode, line, StringComparison.Ordinal);
            Assert.DoesNotContain(setupCode, line, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", line, StringComparison.OrdinalIgnoreCase);
        }
    }
}
