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
        var hasLink = shortcutUrl is not null && Uri.TryCreate(shortcutUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

        // With a published iCloud link the Shortcut installs in one tap and its import question asks for the code.
        // Without one (before the link exists, or in a fork) the page carries the full build sheet.
        var getShortcut = hasLink
            ? $"""<a class="button" href="{Encode(shortcutUrl!)}">Get the Shortcut</a>"""
            : BuildSteps;
        var finish = hasLink
            ? """
              <h2>Paste the code</h2>
              <p>When the Shortcut asks for the setup code, paste it. Run it once and choose <strong>Always Allow</strong>. Then run "Paste from PC" any time to bring over what you copied on the PC.</p>
              """
            : """
              <h2>Run it once</h2>
              <p>Tap ▶ in the Shortcut editor and choose <strong>Always Allow</strong> when asked to connect. Your PC then shows <strong>Paired</strong>. Run "Paste from PC" any time to bring over what you copied on the PC.</p>
              """;

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
                {{finish}}
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

    /// <summary>The Shortcut build sheet (mirrors shortcut/BUILD.md, Shortcut version 1).</summary>
    private const string BuildSteps = """
        <p>Apple does not let apps install shortcuts, so this one is built by hand once (about 15 minutes). After that it can be shared as a one-tap iCloud link.</p>
        <p class="note">Do the first action right away, while the setup code is on your clipboard: this page stops working after 10 minutes. If it does, start a new pairing on the PC.</p>
        <details open>
          <summary>Build steps</summary>
          <p>Open the <strong>Shortcuts</strong> app, tap <strong>+</strong>, and name it <strong>Paste from PC</strong>. Add these actions in order. Tap a blue placeholder to fill it in; to insert a variable, tap the field and pick it above the keyboard.</p>
          <ol class="steps">
            <li><b>Text</b>: paste the setup code.</li>
            <li><b>Split Text</b>: Separator <b>Custom</b>, a single <code>.</code></li>
            <li><b>Get Item from List</b>: <b>Item At Index</b>, <code>2</code>.</li>
            <li><b>Base64 Encode</b>: tap Encode and change it to <b>Decode</b>.</li>
            <li><b>Get Dictionary from Input</b>, then <b>Set Variable</b> <code>Config</code>.</li>
            <li><b>Get Dictionary Value</b>: key <code>u</code> in Config, then <b>Set Variable</b> <code>BaseUrl</code>.</li>
            <li><b>Get Dictionary Value</b>: key <code>t</code> in Config, then <b>Set Variable</b> <code>Token</code>.</li>
            <li><b>If</b> <i>Shortcut Input</i> <b>is</b> <code>auto</code>: <b>Text</b> <code>1</code> → <b>Set Variable</b> <code>New</code>; <b>Text</b> <code>1</code> → <b>Set Variable</b> <code>Quiet</code>. <b>Otherwise</b>: the same with <code>0</code>. <b>End If</b>. At the top of the editor set <b>Receive Text from Nowhere</b>, and <b>If there's no input: Continue</b>.</li>
            <li><b>Text</b>: <i>BaseUrl</i><code>/v1/meta?new=</code><i>New</i> (no spaces).</li>
            <li><b>Get Contents of URL</b>: URL <i>Text</i>. <b>Show More</b>: Method <b>GET</b>; add headers <code>Authorization</code> = <code>Bearer</code>, one space, <i>Token</i>; and <code>X-MistyStep-Shortcut</code> = <code>1</code>.</li>
            <li><b>Get Dictionary from Input</b>, then <b>Set Variable</b> <code>Meta</code>.</li>
            <li><b>Get Dictionary Value</b>: key <code>error</code> in Meta. <b>If</b> it <b>has any value</b>: <b>Get Dictionary Value</b> key <code>message</code> in Meta, <b>Show Alert</b> with it, <b>Stop This Shortcut</b>. <b>End If</b>.</li>
            <li><b>Get Dictionary Value</b>: key <code>type</code> in Meta → <b>Set Variable</b> <code>Type</code>. Key <code>pc</code> → <b>Set Variable</b> <code>PC</code>.</li>
            <li><b>If</b> Type <b>is</b> <code>text</code>: <b>Get Dictionary Value</b> key <code>text</code> in Meta, <b>Copy to Clipboard</b>, and if Quiet <b>is not</b> <code>1</code>, <b>Show Notification</b> "Text copied from <i>PC</i>".</li>
            <li><b>Otherwise</b>, <b>If</b> Type <b>is</b> <code>image</code>: <b>Get Dictionary Value</b> key <code>id</code> in Meta; <b>Text</b> <i>BaseUrl</i><code>/v1/data?id=</code><i>Dictionary Value</i>; <b>Get Contents of URL</b> with the same GET method and two headers; <b>Copy to Clipboard</b>; if Quiet is not 1, notify "Image copied from <i>PC</i>". <b>Otherwise</b>: if Quiet is not 1, notify "Nothing new on <i>PC</i>". Close both <b>End If</b>s.</li>
          </ol>
          <p class="note">Optional near-automatic mode: in Shortcuts, Automation → + → App → pick the apps you paste into → Is Opened → Run Immediately, with one action: Run Shortcut "Paste from PC" with input <code>auto</code>.</p>
        </details>
        """;

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
              details { margin: 12px 0 0; }
              summary { min-height: 44px; padding: 10px 0; font-weight: 600; cursor: pointer; }
              ol.steps { list-style: decimal; padding-left: 22px; margin: 8px 0; }
              ol.steps li { counter-increment: none; background: none; border: 0; border-radius: 0; padding: 0; margin: 0 0 10px; }
              code { font: 15px ui-monospace, Menlo, monospace; background: var(--bg); border: 1px solid var(--border); border-radius: 4px; padding: 0 4px; }
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
