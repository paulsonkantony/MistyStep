using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace MistyStep.Server;

/// <summary>
/// The self-contained pairing page served to the iPad's browser: inline CSS and JavaScript, no external
/// resources, dark mode, large tap targets. <c>navigator.clipboard</c> is unavailable on plain-HTTP pages,
/// so copying uses a visible read-only textarea and <c>document.execCommand('copy')</c> in the tap handler.
/// </summary>
internal static class PairingPage
{
    private const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'";

    public static Task WriteSetupAsync(HttpContext context, string setupCode, string deviceName, string pcName, string? shortcutUrl)
    {
        var code = Encode(setupCode);
        var getShortcut = shortcutUrl is not null && Uri.TryCreate(shortcutUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? $"""<a class="button" href="{Encode(shortcutUrl)}">Get the Shortcut</a>"""
            : """<p class="note">Install the "Paste from PC" Shortcut. Ask whoever shared Misty Step with you for the link.</p>""";

        var body = $$"""
            <h1>Pair with {{Encode(pcName)}}</h1>
            <p class="lead">This device is paired as <strong>{{Encode(deviceName)}}</strong>. Three steps to finish:</p>
            <ol>
              <li>
                <h2>Copy the setup code</h2>
                <textarea id="code" readonly rows="4" aria-label="Setup code">{{code}}</textarea>
                <button id="copy" class="button" type="button">Copy setup code</button>
                <p id="status" class="note" role="status"></p>
              </li>
              <li>
                <h2>Get the Shortcut</h2>
                {{getShortcut}}
              </li>
              <li>
                <h2>Paste the code</h2>
                <p>When the Shortcut asks for the setup code, paste it. Then run "Paste from PC" any time to bring over what you copied on the PC.</p>
              </li>
            </ol>
            <p class="note">Keep the setup code private: it lets a device read what you copy on your PC. This page stops working in 10 minutes.</p>
            <script>
              (function () {
                var area = document.getElementById('code');
                var status = document.getElementById('status');
                document.getElementById('copy').addEventListener('click', function () {
                  area.focus();
                  area.select();
                  area.setSelectionRange(0, area.value.length);
                  var ok = false;
                  try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
                  status.textContent = ok ? 'Copied.' : 'Select the code above and copy it.';
                });
              })();
            </script>
            """;
        return WriteAsync(context, StatusCodes.Status200OK, "Pair with Misty Step", body);
    }

    public static Task WriteGoneAsync(HttpContext context) => WriteAsync(context, StatusCodes.Status410Gone, "Pairing link expired", """
        <h1>This pairing link no longer works</h1>
        <p class="lead">It has expired or was already used on another device.</p>
        <p>On your PC, open Misty Step from the system tray and choose <strong>Pair a device</strong> to get a new code.</p>
        """);

    public static Task WriteLockedOutAsync(HttpContext context) => WriteAsync(context, StatusCodes.Status429TooManyRequests, "Too many attempts", """
        <h1>Too many attempts</h1>
        <p class="lead">Wait five minutes, then scan the code on your PC again.</p>
        """);

    private static async Task WriteAsync(HttpContext context, int status, string title, string body)
    {
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="referrer" content="no-referrer">
            <title>{{Encode(title)}}</title>
            <style>
              :root { color-scheme: light dark; --bg: #f6f7f9; --fg: #16181d; --muted: #5b616e; --card: #ffffff; --accent: #2457d6; --accent-fg: #ffffff; --border: #d9dce3; }
              @media (prefers-color-scheme: dark) { :root { --bg: #111317; --fg: #eceef2; --muted: #a3a9b6; --card: #1b1e24; --accent: #6d95ff; --accent-fg: #0b1020; --border: #30343d; } }
              * { box-sizing: border-box; }
              body { margin: 0; padding: 24px 16px 48px; background: var(--bg); color: var(--fg); font: 17px/1.5 -apple-system, system-ui, sans-serif; }
              main { max-width: 560px; margin: 0 auto; }
              h1 { font-size: 26px; line-height: 1.25; margin: 0 0 8px; }
              h2 { font-size: 19px; margin: 0 0 8px; }
              .lead { color: var(--muted); margin: 0 0 20px; }
              ol { list-style: none; counter-reset: step; padding: 0; margin: 0 0 20px; }
              li { counter-increment: step; background: var(--card); border: 1px solid var(--border); border-radius: 14px; padding: 16px; margin: 0 0 12px; }
              li h2::before { content: counter(step) ". "; color: var(--accent); }
              textarea { width: 100%; font: 15px/1.4 ui-monospace, Menlo, monospace; padding: 12px; border-radius: 10px; border: 1px solid var(--border); background: var(--bg); color: var(--fg); resize: none; word-break: break-all; -webkit-user-select: text; user-select: text; }
              .button { display: block; width: 100%; min-height: 52px; margin: 12px 0 0; padding: 14px 16px; border: 0; border-radius: 12px; background: var(--accent); color: var(--accent-fg); font: 600 18px/1.3 -apple-system, system-ui, sans-serif; text-align: center; text-decoration: none; cursor: pointer; }
              .note { color: var(--muted); font-size: 15px; margin: 8px 0 0; }
            </style>
            </head>
            <body>
            <main>
            {{body}}
            </main>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = bytes.Length;
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers["Referrer-Policy"] = "no-referrer";
        await response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
