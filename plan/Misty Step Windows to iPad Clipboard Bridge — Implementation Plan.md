# Misty Step: Windows to iPad Clipboard Bridge — Implementation Plan

Nov 14, 2026

## Summary

Misty Step is a Windows tray app plus one Apple Shortcut: anything you copy on the PC, text or image, lands on the iPad clipboard after one tap. "Misty Step" is a working name; check that it is free before release.

**Goals**

- One direction only: Windows to iPad. The same Shortcut also works on an iPhone.
- Text and images only.
- No cloud and no account. Nothing leaves the PC until a paired device asks for it.
- Standalone and shareable: one installer, guided pairing in under three minutes, no networking knowledge needed.
- Safe defaults: one revocable token per device, encrypted transport recommended, password-manager copies skipped.

**Not in version 1**

- iPad to Windows.
- Files, rich text, HTML, or clipboard history.
- Paste on the iPad with zero taps. iPadOS does not allow it (see Platform constraints).
- A native iPad app, or senders other than Windows.

**How to use this document.** Everything from Platform constraints through Packaging is the specification. Build order gives the milestones with acceptance criteria. Open questions lists what must be checked on real devices, because documentation could not confirm it.

## Platform constraints

The iPad cannot be pushed to, so the PC serves and the iPad pulls. Every other design choice follows from the rows below.

| Constraint | Design consequence |
| --- | --- |
| Third-party apps on iPadOS reach the clipboard only while in the foreground ([BetaNews](https://betanews.com/article/apple-iphone-windows-clipboard-sync-eu/)). | The iPad side is a Shortcut the user triggers. No native iPad app is built. |
| The Shortcuts action "Get Contents of URL" accepts custom request headers ([Indigo docs](https://docs.indigodomo.com/2025.2/user/automation/apple-shortcuts/)). | The token travels in an `Authorization: Bearer` header, never in the URL. |
| A shortcut is assumed unable to read HTTP status codes or response headers (verify in M4). | Everything the Shortcut branches on is in a JSON body, including errors. |
| A failed network request is assumed to stop a shortcut with a system error (verify in M4). | The quiet automation variant is gated so it does not run when the PC is unreachable. |
| Automations can be set to Run Immediately instead of asking first; a notification is still shown ([Cassinelli](https://matthewcassinelli.com/automations-run-immediately-shortcuts-notifications/)). | An optional "fetch when I open app X" automation gives near-automatic sync. |
| Shortcuts are shared by iCloud link or file, and import questions personalise them on install ([Apple](https://support.apple.com/guide/shortcuts/apdf01f8c054/ios)). | Pairing hands the user one setup code to paste into a single import question. |
| The Windows clipboard holds several formats at once, and password managers mark secrets with documented formats ([CopyQ docs](https://copyq-de.readthedocs.io/de/stable/security.html)). | Capture uses a fixed format priority and skips anything marked secret. |
| Windows Firewall blocks inbound connections by default. | The installer adds one narrowly scoped inbound rule. |
| New downloads show a SmartScreen warning until they build reputation, signed or not ([Microsoft Learn](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options)). | Releases are signed with one consistent publisher identity from the first public build. |
| Apple plans its own Windows clipboard sync, estimated for fall 2027 ([SamMobile](https://www.sammobile.com/news/galaxy-books-windows-access-iphone-clipboard-items-future/)) and probably EU-only at first ([tbreak](https://tbreak.com/iphone-to-windows-clipboard-sync-eu-apple-confirms/)). | The app has at least a year of useful life. Keep it small and easy to retire. |

## Architecture

The PC keeps exactly one current item in memory and serves it on request. The iPad Shortcut fetches it and writes it to the iPad clipboard.

```
 Windows PC
 ┌───────────────────────────────────────────────────────────────────────────────┐
 │  Any app copies ──► Clipboard watcher ──► Privacy filter ──► Clip store       │
 │                     (WM_CLIPBOARDUPDATE,   and normaliser    (one item,       │
 │                      150 ms debounce)      (text / PNG)      expires)         │
 │                                                                  ▲            │
 │                                                                  │ read       │
 │                                          HTTP server (Kestrel) ──┘            │
 │                                          address filter, token, rate limit    │
 └──────────────────────────────────────────────────▲───────────┬────────────────┘
                                                    │           │
                         GET /v1/meta (+ /v1/data)  │           │  JSON (text inline)
                         Authorization: Bearer …    │           │  or image bytes
                                                    │           ▼
 iPad ┌───────────────────────────────────────────────────────────────────────────┐
      │  User taps "Paste from PC" ──► Get Contents of URL ──► Copy to Clipboard  │
      │                                                         ──► user pastes   │
      └───────────────────────────────────────────────────────────────────────────┘
```

Read the PC box left to right, then follow the arrows down to the iPad. The only traffic between the devices is the Shortcut's request and the PC's answer.

**Components**

- **Clipboard watcher** (Windows, UI thread): a hidden window subscribed to clipboard change notifications.
- **Privacy filter and normaliser**: decides whether to capture, picks text or image, and encodes images as PNG.
- **Clip store**: one immutable current item with id, type, payload and capture time. It expires after a time limit.
- **HTTP server** (Kestrel, in-process): four routes. It checks the caller's address, token and rate limit on every request.
- **Device registry**: paired devices, token hashes, last-seen time, and the last item id delivered to each device.
- **Tray UI**: status, pause, pairing wizard, device list, settings.
- **"Paste from PC" Shortcut** (iPad): fetch, branch on type, copy to clipboard.

**Copy-to-paste sequence**

1. The user copies in any Windows app. Windows posts `WM_CLIPBOARDUPDATE` to the watcher.
2. After a 150 ms debounce, the watcher reads the available formats on the UI thread.
3. The filter drops the copy if it is marked secret, comes from an excluded app, is oversize, or sharing is paused.
4. The normaliser builds a `ClipItem`. Image encoding runs on the thread pool.
5. The store swaps in the new item atomically and the tray tooltip updates.
6. The user taps the Shortcut. It calls `GET /v1/meta` with its token.
7. The server answers with JSON. Text is inline; for an image the Shortcut then calls `GET /v1/data`.
8. The Shortcut copies the result to the iPad clipboard and the user pastes.

**Threading rules**

- One STA UI thread owns the tray icon, all windows and every clipboard read.
- Kestrel runs on its own threads and never touches Win32 clipboard APIs.
- The only shared mutable state is the current-item reference and the device registry. The first is swapped with `Interlocked.Exchange`; the second is guarded by one lock.
- The app never writes to the Windows clipboard, so it cannot trigger itself.

## Tech stack and repository layout

Build it in C# on .NET 10 with WinForms for the tray and Kestrel for HTTP. .NET 10 is the current long-term-support release, supported until November 14, 2028; .NET 8 and 9 went out of support on November 10, 2026 ([Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)).

| Piece | Choice | Reason |
| --- | --- | --- |
| Language and runtime | C# 14, .NET 10, `net10.0-windows` | First-class Windows clipboard, tray and registry APIs; supported through 2028. |
| Tray and windows | WinForms (`NotifyIcon`, plain forms) | Smallest dependable option for a tray app. No XAML needed for three small windows. |
| HTTP server | Kestrel via `FrameworkReference Microsoft.AspNetCore.App`, minimal APIs | Hardened HTTP parsing, limits and timeouts. `HttpListener` is rejected because it needs a URL reservation made as admin. |
| JSON | `System.Text.Json` with source generation | No extra dependency. |
| QR code | `QRCoder` (MIT) | Renders the pairing QR to a bitmap. |
| Logging | `Microsoft.Extensions.Logging` plus a small rolling file provider | Avoids a logging framework dependency. |
| Tests | xUnit, `Microsoft.AspNetCore.TestHost` | In-memory API tests without opening a port. |
| Installer | Inno Setup 6 | Free, scriptable, handles elevation for the firewall rule. |
| Publish | Self-contained, single file, `win-x64` and `win-arm64` | Users install nothing else. Do not enable trimming; WinForms does not support it. |

Python with PyInstaller was rejected for sharing: large bundles and frequent antivirus false positives. Go was the runner-up, with a smaller binary but hand-written Win32 clipboard code.

**Repository layout**

All paths are relative to the repository root; there is no extra top-level folder.

```
<repository root>/
  MistyStep.sln
  plan/                 this document
  src/
    MistyStep.Core/       models, clip store, device registry, auth, rate limiter,
                          address rules, setup-code codec. No UI, no Win32.
    MistyStep.Windows/    clipboard watcher and reader, network probe, firewall check,
                          autostart, hotkey, toast. All P/Invoke lives here.
    MistyStep.Server/     Kestrel host, middleware, endpoints, pairing page.
    MistyStep.App/        entry point, tray, pairing wizard, settings, device list.
  tests/
    MistyStep.Core.Tests/
    MistyStep.Server.Tests/
  installer/mistystep.iss
  shortcut/BUILD.md       action-by-action sheet for building the Shortcut by hand
  docs/                   SECURITY.md, PRIVACY.md, TROUBLESHOOTING.md
  .github/workflows/      build.yml, release.yml
```

**Interfaces to define first.** Everything in Core depends only on these, so it can be tested without Windows APIs.

- `IClipboardSource`: raises `Captured(RawClip)`; implemented by the Win32 watcher and by a fake in tests.
- `IClipStore`: `Current`, `Set(ClipItem)`, `Clear()`, expiry.
- `IDeviceRegistry`: create pairing code, redeem, authenticate, revoke, record delivery.
- `INetworkProbe`: Tailscale address and name, LAN addresses, network category.
- `IClock`: monotonic time, so expiry tests do not sleep.

## Windows clipboard capture

Each copy produces at most one `ClipItem`: text if the clipboard has any, otherwise an image, otherwise nothing. A copy that is ignored never changes the stored item.

**Listening**

- Create a message-only window on the UI thread and call `AddClipboardFormatListener`. Handle `WM_CLIPBOARDUPDATE` (0x031D). Remove the listener on exit.
- Debounce by 150 ms: restart a timer on each message and read once when it fires. Excel and Office raise several updates per copy.
- Store `GetClipboardSequenceNumber()` at each read and skip the read if it has not changed.
- Watchdog every 30 s: if the sequence number moved without a message, read now and re-register the listener.

**Reading**

- Use raw Win32 calls (`OpenClipboard`, `EnumClipboardFormats`, `GetClipboardData`, `GlobalLock`, `GlobalSize`), not `System.Windows.Forms.Clipboard`. This gives access to the registered `PNG` format and control over retries.
- `OpenClipboard` fails when another app holds the clipboard. Retry up to 8 times with 25, 50, then 100 ms waits, one second in total. Then give up and log at debug level.
- List the formats first and decide before reading any data. Copy bytes out, then `CloseClipboard` in a `finally` block.

**Decision order**

1. Sharing is paused, or the mode is Manual and this read was not started by the hotkey: ignore.
2. A secrecy marker is present: ignore. Markers are the formats `ExcludeClipboardContentFromMonitorProcessing` and `Clipboard Viewer Ignore`, or `CanIncludeInClipboardHistory` or `CanUploadToCloudClipboard` holding a DWORD of 0.
3. The clipboard owner's process is on the excluded-apps list: ignore. Resolve it with `GetClipboardOwner`, then `GetWindowThreadProcessId`. A null owner is not excluded.
4. `CF_UNICODETEXT` is present and not only whitespace: make a text item. Convert CRLF to LF and drop a trailing NUL. If the UTF-8 size exceeds the text limit, ignore and show a tray hint.
5. Otherwise look for an image, in this order:
   - the registered `PNG` format: validate the signature, read width and height, use the bytes unchanged;
   - `CF_DIBV5`, then `CF_DIB`: convert to PNG;
   - `CF_HDROP` with exactly one file ending in .png, .jpg, .jpeg, .gif or .webp and within the image limit: serve the file bytes unchanged with the matching MIME type. Convert a single .bmp to PNG.
6. Anything else (several files, other file types, empty clipboard): ignore.

**Image handling**

- Copy pixel data out of clipboard memory on the UI thread, then encode on the thread pool with `System.Drawing`.
- For 32-bit DIBs, treat alpha as real only if at least one pixel has non-zero alpha. Otherwise force the image opaque. Many apps leave alpha at zero.
- Downscale so the long edge is at most 4096 px (setting). If the PNG still exceeds the image limit and has no alpha, re-encode as JPEG at quality 90. If it still does not fit, ignore and show a tray hint.
- Tag each read with a generation number. If a newer copy arrives during encoding, discard the older result.

**The item model**

```csharp
public enum ClipType { Text, Image }

public sealed record ClipItem(
    string Id,                  // 16 random bytes as hex; new for every capture
    ClipType Type,
    string? Text,               // set when Type == Text
    ReadOnlyMemory<byte> Bytes, // set when Type == Image
    string Mime,                // text/plain, image/png, image/jpeg, image/gif, image/webp
    int Width, int Height,      // 0 for text
    bool IsUrl,                 // Text is one absolute http or https URL
    long CapturedAt);           // IClock monotonic milliseconds
```

The id is random, not a content hash, so copying the same thing twice counts as a new item.

**Expected results by source**

| Copy source | Clipboard holds | Result |
| --- | --- | --- |
| Notepad, browser text, Word | Unicode text, maybe RTF and HTML | Plain text |
| Excel cells | Text plus a bitmap | Text |
| Browser "Copy image" | PNG, DIB, HTML, no plain text | Image from PNG |
| Win+Shift+S, Snipping Tool, Print Screen | Bitmap, often PNG | Image |
| Explorer, one image file | File drop list | That image |
| Explorer, several files or a non-image | File drop list | Ignored |
| Password manager | Text plus a secrecy marker | Ignored |
| Remote Desktop or VM clipboard | Same as local, repeated updates | Same as local; debounce absorbs repeats |

## HTTP API

The server exposes four GET routes on one port (default 45654) and accepts no request bodies. Authenticated routes always answer with a body the Shortcut can branch on.

| Route | Auth | Purpose | Response |
| --- | --- | --- | --- |
| `GET /v1/meta` | Bearer token | Describe the current item; text is returned inline | 200 JSON |
| `GET /v1/data` | Bearer token | Bytes of the current image | 200 with `Content-Type` and `Content-Length`; 204 if the current item is not an image |
| `GET /v1/ping` | Bearer token | Connectivity check for troubleshooting | 200 JSON `{"v":1,"ok":true,"pc":"...","version":"..."}` |
| `GET /pair?c=<code>` | One-time pairing code | Pairing page for the iPad's browser | 200 HTML; 410 HTML if the code is used or expired |

**`/v1/meta` responses**

```json
{ "v": 1, "type": "text", "id": "9c1f...", "text": "hello", "isUrl": false,
  "ageSeconds": 12, "pc": "MY-LAPTOP" }

{ "v": 1, "type": "image", "id": "5ab0...", "mime": "image/png",
  "bytes": 183322, "width": 1920, "height": 1080, "ageSeconds": 4, "pc": "MY-LAPTOP" }

{ "v": 1, "type": "none", "reason": "expired", "pc": "MY-LAPTOP" }

{ "v": 1, "error": "unauthorized", "message": "This device is not paired. Pair again from Misty Step on your PC." }
```

- `reason` is one of `empty`, `expired`, `paused`, `unchanged`.
- `?new=1` makes the server answer `unchanged` when this device already received the current item. The server tracks this per device, so the Shortcut stores no state.
- An item counts as delivered when `/v1/meta` returns it as text, or when `/v1/data` finishes writing the body.
- `/v1/data` always serves the current image. An optional `?id=` is logged for diagnostics only.
- Errors on `/v1` use the JSON shape above with HTTP status 200. A 200 always reaches the Shortcut, while a 401 body may not (see the M4 open questions). Error codes: `unauthorized`, `rate_limited`. Where this plan says "answer 429" or "401", read it as that error body. The HTML pairing page keeps its 410 and 429 statuses.

**Request pipeline, in order**

1. Connection filter: the remote address must be allowed by the active network mode (see Networking). If not, abort the connection without a response.
2. Only GET on the four known paths, with a Host header that matches a known host (see Security model). Anything else gets 404 with an empty body.
3. Lockout check for the remote address.
4. Authentication on `/v1/*`: read `Authorization: Bearer <token>`, hash with SHA-256, look up the device, compare with `CryptographicOperations.FixedTimeEquals`.
5. Handler.
6. Add `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. Set `AddServerHeader = false`.

**Kestrel limits**

| Setting | Value |
| --- | --- |
| Protocols | HTTP/1.1 only |
| `MaxRequestBodySize` | 0 |
| `MaxRequestHeadersTotalSize` | 8 KB |
| `MaxRequestLineSize` | 2 KB |
| `RequestHeadersTimeout` | 5 s |
| `KeepAliveTimeout` | 30 s |
| `MaxConcurrentConnections` | 16 |

**Abuse limits**

- Five failed authentications from one address within 60 s lock that address out for 5 minutes with 429.
- Keep at most 1,000 lockout entries; evict the oldest.
- Six failed pairing-code attempts in total cancel every outstanding pairing code.
- Per device, allow at most 20 requests per second; answer 429 above that.

## Pairing and the iPad Shortcut

Pairing takes one QR scan and one paste. The Shortcut itself cannot be generated by code: build it once by hand on an iPad from the sheet below, then share it by iCloud link.

**Pairing flow**

1. The user picks "Pair a device" in the tray menu.
2. The wizard shows the detected connection options and preselects Tailscale when it is running (see Networking).
3. Preflight checks run: server listening, firewall rule present, address resolved. Each failure shows a specific fix.
4. The app creates a pairing code (16 random bytes as hex, valid 10 minutes) and shows a QR code for `http://<host>:<port>/pair?c=<code>`, with the URL as text underneath.
5. The user scans the QR with the iPad camera. Safari opens the pairing page. The page loading proves the iPad can reach the PC.
6. On first load the server redeems the code: it mints a device token, stores only its SHA-256 hash, and names the device from the user agent and date. The user can rename it later.
7. The same remote address may reload the page until the code expires. Any other address gets 410. The plaintext token is dropped from memory at expiry.
8. The pairing page walks through three steps: Copy setup code, Get the Shortcut (iCloud link), paste the code when the Shortcut asks.
9. The wizard shows "Paired" when the device makes its first authenticated `/v1/meta` call.

**Setup code**

```
MS1.<standard Base64 of UTF-8 JSON>
{"u":"http://100.101.102.103:45654","t":"<43-char token>","n":"MY-LAPTOP"}
```

The token is 32 bytes from `RandomNumberGenerator`, encoded as base64url. The outer encoding is standard Base64 because that is what the Shortcuts decode action is expected to accept.

**Pairing page requirements**

- One self-contained HTML response with inline CSS and JavaScript. No external resources. Supports dark mode and large tap targets.
- `navigator.clipboard` is unavailable on plain-HTTP pages. Copy with a visible read-only `textarea` and `document.execCommand('copy')` inside the tap handler. Keep the text selectable as a fallback.
- Send `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'` and `Referrer-Policy: no-referrer`.

**Shortcut build sheet: "Paste from PC"**

1. **Text**: holds the setup code. Add an import question: "Paste the setup code from the Misty Step pairing page".
2. **Split Text** by custom separator `.`, **Get Item from List** (index 2), **Base64 Encode** set to Decode, **Get Dictionary from Input**. Save as variable `Config`.
3. **If** Shortcut Input is `auto`, set variable `New` to 1 and `Quiet` to 1. Otherwise set both to 0.
4. **Get Contents of URL**: `Config.u` + `/v1/meta?new=` + `New`. Method GET. Headers: `Authorization` = the word `Bearer`, exactly one space, then `Config.t` (no leading space; the result looks like `Bearer abc123…`), and `X-MistyStep-Shortcut` = `1`. Save the result as dictionary `Meta`.
5. **If** `Meta.error` has any value: **Show Alert** with `Meta.message`, then **Stop This Shortcut**.
6. **If** `Meta.type` is `text`: get `Meta.text`, **Copy to Clipboard**. Unless `Quiet`, **Show Notification** "Text copied from" + `Meta.pc`.
7. **Otherwise if** `Meta.type` is `image`: **Get Contents of URL** `Config.u` + `/v1/data?id=` + `Meta.id` with the same headers, then **Copy to Clipboard**. Unless `Quiet`, notify "Image copied from" + `Meta.pc`.
8. **Otherwise**: unless `Quiet`, notify "Nothing new on" + `Meta.pc`.

Input `open` is a third mode: when `Meta.isUrl` is true, run **Open URLs** on the text instead of copying it.

**Ways to trigger it on the iPad**

- Dock or Home Screen icon, Home Screen widget, or a Control Center control.
- Spotlight with a hardware keyboard: Cmd+Space, type "Paste", Return.
- Apple Pencil Pro squeeze, or Siri by name.

**Optional near-automatic mode** (dropped from the docs after M4: the App trigger fires only when an app opens, so it cannot keep the clipboard current. The docs recommend an AssistiveTouch button or a Control Center control instead. The `auto` input stays in the Shortcut.)

Create a personal automation: App, choose the apps you paste into, Is Opened, Run Immediately. Its one action is Run Shortcut "Paste from PC" with input `auto`. The iPad clipboard is replaced only when the PC holds an item this iPad has not fetched. In home Wi-Fi mode, start the automation with a network-name check so it does not run elsewhere.

## Networking modes

Support two modes and recommend Tailscale: it encrypts traffic and works on any network. Home Wi-Fi mode is plain HTTP and is offered only on networks Windows marks as Private.

|  | Tailscale (recommended) | Home Wi-Fi |
| --- | --- | --- |
| Works when | Both devices are online anywhere | Both are on the same network |
| Encryption in transit | Yes, WireGuard between the two devices | None |
| Host in the setup code | MagicDNS name, else the 100.x address | `<hostname>.local`, else the LAN IP |
| Allowed callers | 100.64.0.0/10 | Subnet of a local interface on a Private network |
| User needs | Tailscale installed and signed in on both devices | Wi-Fi set to Private in Windows |

Both modes can be on at once. Each paired device records the base URL it was given.

**Listening.** Bind `0.0.0.0:<port>` (IPv4 only) and filter by remote address, instead of binding to specific addresses. Tailscale often starts after the app at sign-in, and addresses change on resume and network switches. The firewall rules and the filter give the same exposure without rebind logic.

**Address policy** (pure function in Core, unit tested)

- Loopback: always allowed.
- Tailscale mode on: allow 100.64.0.0/10.
- Home Wi-Fi mode on: allow addresses inside the subnet of any local IPv4 interface whose network category is Private. Skip Tailscale and virtual adapters.
- Everything else: abort the connection.

**Detecting Tailscale**

- Look for an interface that is up and holds an IPv4 address in 100.64.0.0/10.
- For the MagicDNS name, run `tailscale status --json` with a 3 s timeout and read `Self.DNSName`. Look for `tailscale.exe` on the PATH and in `%ProgramFiles%\Tailscale`. Fall back to the 100.x address.
- If Tailscale is missing, the wizard explains in two lines what it is, links to its download page, and offers Home Wi-Fi mode instead.

**Home Wi-Fi addresses**

- Read the network category through the `INetworkListManager` COM interface.
- Try `<hostname>.local` first. The pairing page loading on the iPad is the real test.
- If the iPad has not connected after 60 s, offer "Try the IP address instead" and redraw the QR. Warn that the IP can change and suggest a DHCP reservation.

**Firewall rules** (created by the installer, which runs elevated)

```
netsh advfirewall firewall add rule name="Misty Step (Tailscale)" dir=in action=allow ^
  program="<install dir>\MistyStep.exe" protocol=TCP localport=45654 ^
  remoteip=100.64.0.0/10 profile=any

netsh advfirewall firewall add rule name="Misty Step (Home Wi-Fi)" dir=in action=allow ^
  program="<install dir>\MistyStep.exe" protocol=TCP localport=45654 ^
  remoteip=LocalSubnet profile=private
```

- Preflight reads rule state through `INetFwPolicy2`, which needs no elevation.
- Changing the port relaunches `MistyStep.exe --update-firewall <port>` with the `runas` verb, which shows one UAC prompt.
- The uninstaller removes both rules.

**Port.** Default 45654. If it is taken at startup, show the error state and name the port. Never pick another port silently: the base URL is stored in every paired Shortcut. Changing host or port means pairing again and pasting the new setup code into the Shortcut's first Text action (step 1 of the build sheet), replacing the old code.

## Security model

A clipboard regularly holds passwords and private text. The design therefore treats every other device on the network as untrusted and assumes a setup code can leak.

| Threat | Mitigation | What remains |
| --- | --- | --- |
| Another device on the network requests the clipboard | Per-device token, remote-address filter, scoped firewall rules, lockout after 5 failures | Nothing known in Tailscale mode |
| Someone captures Wi-Fi traffic | Tailscale mode is encrypted end to end. Home Wi-Fi mode runs only on Private networks and is labelled "not encrypted" in the wizard and settings | In Home Wi-Fi mode, anyone on that Wi-Fi who captures traffic can read items and the token |
| A token leaks (shared setup code, screenshot, synced Shortcut) | One token per device, revoke in the device list, last-seen time shown, optional toast on every fetch | A leak can go unnoticed until used |
| A secret is copied by accident | Secrecy markers honoured, excluded apps, pause, Manual mode, 10-minute expiry, "Clear shared item" in the tray | Secrets copied from apps that set no marker |
| Someone nearby scans the pairing QR first | 10-minute life, code bound to the first caller's address, the wizard shows which device connected, Cancel revokes it | A visible, cancellable hijack |
| A web page in the PC's browser calls the local server (CSRF, DNS rebinding) | Token must be in a header, no cookies, no CORS headers, `Host` header must match a known host name or address | None known |
| Tampered installer | Signed releases, SHA-256 checksums on the release page, CI builds from tagged commits, one third-party package | Trust in the build pipeline |
| Malware already on the PC | Out of scope: it can read the clipboard directly | Not addressed |

**Rules for the implementation**

- Never log clipboard content, tokens, pairing codes, setup codes or `Authorization` headers. Log type, size, device name and outcome only.
- Never write clipboard content to disk. No temp files, no history, no content in crash reports.
- Store only SHA-256 hashes of tokens. The plaintext exists in memory from pairing until the pairing code expires, then is cleared.
- Take every random value from `RandomNumberGenerator`.
- Validate the `Host` header against: the Tailscale address and MagicDNS name, `<hostname>.local`, current LAN addresses, `localhost` and `127.0.0.1`, each with the configured port.
- Make no outbound network calls except the update check, which the user opts into during first run.
- No telemetry of any kind. State this in `PRIVACY.md`.
- Ship a `SECURITY.md` with a private contact for vulnerability reports.

## Robustness

The app must run for weeks unattended, so each failure below has one required behaviour. None of them may show clipboard content in an error.

| Situation | Required behaviour |
| --- | --- |
| Another app holds the clipboard | Retry on the schedule in Capture, then skip quietly |
| Burst of clipboard updates | Debounce; one read per burst |
| Listener stops receiving messages (session switch, Remote Desktop reconnect, Explorer restart) | The 30 s watchdog notices the sequence number moved and re-registers |
| Very large image | Downscale or skip per Capture. Never block the UI thread. Hold one item only and release the previous one immediately |
| A fetch arrives while an image is still encoding | Serve the previous item. The store swaps only when the new item is complete |
| iPad disconnects mid-download | Do not count the item as delivered. Log at debug level |
| App starts before the network is up | The server is already listening. Re-run the network probe on `NetworkChange` events and update the tray status |
| Sleep and resume | Re-run the probe on `PowerModeChanged` Resume. Expiry uses `Environment.TickCount64`, so wall-clock changes have no effect |
| Port already in use | Error state naming the port. Never change port silently |
| Second instance launched | Named mutex. The second process signals the first to open its menu, then exits |
| Two Windows users signed in | Each has their own instance. The second gets the port-in-use error with an explanation |
| Config file missing or corrupt | Rename it to `config.bad-<timestamp>.json`, start with defaults, tell the user devices must be paired again |
| Config write interrupted | Write to a temp file, then `File.Replace`. Never leave a half-written config |
| PC renamed, or its Tailscale name changes | Detect that a device's stored host no longer matches. Show a tray warning with a "Pair again" action |
| Unhandled exception | Log it without content and show a tray notice. Call `RegisterApplicationRestart` at startup so Windows restarts the app after a crash |
| Windows shutting down | Remove the listener and stop Kestrel within 2 s |
| PC asleep or off when the Shortcut runs | Nothing the PC can do. Document the Shortcuts error in `TROUBLESHOOTING.md`; the automation variant is gated |
| An older Shortcut calls a newer app | Keep `/v1` stable. Add fields, never rename or remove them. Log the `X-MistyStep-Shortcut` version |

**Health status.** Compute one status from four checks: listener registered, server listening, at least one mode has an address, firewall rule present. The tray icon has three states: sharing, paused, needs attention. The menu's first line names the failing check.

## Quality-of-life features

The whole product is the tray menu, a pairing wizard and three small windows. Everything a user does daily must be reachable in two clicks from the tray.

**Tray menu**

```
Sharing: text, 12 s ago                 (status line, disabled)
----------------------------------------
Share current clipboard now             Ctrl+Alt+C
Clear shared item
Pause sharing      >  5 minutes | 1 hour | Until I resume
Mode               >  Automatic | Manual (hotkey only)
----------------------------------------
Pair a device...
Devices...
Settings...
Help and troubleshooting
----------------------------------------
Start with Windows                      [x]
Quit
```

**Version 1.0**

- Status line and tooltip show the type and age of the shared item, never its content.
- Pause with automatic resume. The icon changes while paused.
- Manual mode: nothing is shared until the user presses the global hotkey (`RegisterHotKey`, default Ctrl+Alt+C, configurable, with conflict detection).
- "Clear shared item" empties the store at once.
- Expiry choice: 2 minutes, 10 minutes, 1 hour, or until replaced.
- Devices window: name, last seen, items fetched, Rename, Revoke.
- Fetch feedback: the tray icon pulses when a device fetches. An optional toast says "iPad fetched an image".
- First-run wizard: welcome, Start with Windows, update-check opt-in, then straight into pairing.
- Troubleshooting window: the four health checks, current addresses, port and firewall state, plus "Copy diagnostics" with no secrets in it.
- Tray hints when a copy was skipped for size. No hint for secrets or excluded apps.
- Light and dark tray icons at 100 to 200 percent scaling.

**Version 1.1**

- "Open link from PC": the Shortcut's `open` mode, for handing a URL to Safari.
- Excluded-apps editor with "Add the app I last copied from".
- Image options: maximum long edge, JPEG for large photos.
- Update notice with a link to release notes. No silent self-update.
- Strings moved to resource files so the app can be translated.
- Optional HTTPS in Tailscale mode using Tailscale-issued certificates.
- Re-pair from the Shortcut (owner request, 2026-11-14): when the PC answers `unauthorized`, or on a "Re-pair" choice, the Shortcut asks for the new setup code (Ask for Input) and carries on, so nobody edits its first Text action. A Shortcut cannot edit itself, so the code would live in a file it reads and rewrites, for example `Shortcuts/MistyStep/setup.txt` in iCloud Drive. Check how file permissions and iCloud sync behave before committing to it.

**Deliberately left out**

- A content preview in the tray. It would put clipboard text on screen during screen sharing.
- Clipboard history. It would mean storing content.
- Automatic port changes or self-updating binaries.

## Settings and storage

All settings live in one per-user JSON file. It holds token hashes, never tokens, and the app writes nothing else except logs.

- Config: `%APPDATA%\MistyStep\config.json`
- Logs: `%LOCALAPPDATA%\MistyStep\logs\mistystep-YYYYMMDD.log`. Keep 7 days, 5 MB per file, Information level by default.

| Key | Default | Notes |
| --- | --- | --- |
| `schemaVersion` | 1 | Migrate older files on load |
| `port` | 45654 | Changing it requires re-pairing |
| `mode` | `automatic` | Or `manual` |
| `hotkey` | `Ctrl+Alt+C` | Used by "Share current clipboard now" |
| `expiryMinutes` | 10 | 0 means until replaced |
| `tailscaleEnabled` | true when Tailscale is detected |  |
| `lanEnabled` | false | The user opts in during pairing |
| `maxTextBytes` | 1048576 | 1 MB of UTF-8 |
| `maxImageBytes` | 26214400 | 25 MB |
| `maxImageEdge` | 4096 | Pixels on the long edge |
| `honorSecrecyMarkers` | true |  |
| `excludedApps` | `KeePass.exe`, `KeePassXC.exe`, `1Password.exe`, `Bitwarden.exe` | Process names, case-insensitive |
| `serveWhileLocked` | true | The usual case is walking away with the iPad |
| `notifyOnFetch` | false |  |
| `updateCheck` | false | Set by the first-run question |
| `shortcutUrl` | Built-in iCloud link | Override for forks |
| `devices` | empty list | See below |

Start with Windows is not stored in the file. The `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value is the single source of truth.

**Device record**

```json
{
  "id": "d_4f2a9c",
  "name": "iPad",
  "tokenHash": "<64 hex chars>",
  "baseUrl": "http://my-laptop.tailnet-name.ts.net:45654",
  "createdUtc": "2026-11-14T19:30:00Z",
  "lastSeenUtc": "2026-11-14T19:42:10Z",
  "fetchCount": 17
}
```

The last item id delivered to each device is kept in memory only. Batch `lastSeenUtc` and `fetchCount` writes to at most one per minute.

**Command-line switches**

- `--minimized`: used by autostart; no window on launch.
- `--update-firewall <port>`: elevated helper; rewrites the two firewall rules and exits.
- `--portable`: keep config and logs beside the executable, for the zip build.
- `--log-level debug`: verbose logging for one session.

## Packaging and distribution

Ship a signed installer and a portable zip through GitHub Releases, plus one iCloud link for the Shortcut. A new user installs, scans a QR code, adds the Shortcut and pastes one code.

**Windows build**

```
dotnet publish src/MistyStep.App -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true
```

- Build `win-x64` and `win-arm64`. Measure the output size in M7 and state it in the README.
- Target Windows 11. Treat Windows 10 22H2 as best effort.
- Use SemVer. Show the version in the About box and in `/v1/ping`.
- License the code under MIT and include third-party notices for QRCoder.

**Installer (Inno Setup)**

- Per-machine install into Program Files. Elevation is needed anyway for the firewall rules.
- Creates the two firewall rules, a Start menu entry, and the autostart value if the user ticks it.
- Launches the app at the end, which opens the first-run wizard.
- The uninstaller removes the firewall rules and the autostart value, then asks whether to delete settings.

**Portable zip**

One executable and a README, run with `--portable`. Windows shows its own firewall prompt on first launch. Document that the user must allow Private networks.

**Code signing**

| Option | Cost | Who can use it |
| --- | --- | --- |
| [Azure Artifact Signing](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options) (formerly Trusted Signing) | About $9.99 per month | Individual developers in the USA and Canada; organisations also in the EU and UK |
| OV certificate from a certificate authority | Typically $150 to $300 per year | Worldwide; key must live on a hardware token or HSM |
| [SignPath Foundation](https://signpath.io) | Free | Qualifying open-source projects |

- Signing does not remove the SmartScreen warning on day one. Reputation builds with downloads and carries across releases signed by the same publisher.
- Sign the executable and the installer, with a timestamp.
- Until reputation builds, the README shows the "Windows protected your PC" dialog and how to verify the SHA-256 checksum.

**Release pipeline** (GitHub Actions, on a `v*` tag)

1. Restore, build, run all tests.
2. Publish both architectures.
3. Sign the executables, build the installers, sign the installers.
4. Compute SHA-256 checksums.
5. Create a draft GitHub Release with the artifacts, checksums and notes.

**Update check.** If the user opted in, request the latest release from the GitHub Releases API once a week. If it is newer, add "Update available" to the tray menu. Download and install stay manual.

**Shortcut distribution**

- Build the Shortcut by hand from `shortcut/BUILD.md`, then Share, Copy iCloud Link. Put the link in the app's `shortcutUrl` default and in the README.
- Anyone with the link can read the Shortcut's contents. Confirm the shared copy's Text action is empty before publishing.
- iCloud links have failed before, so keep `BUILD.md` with screenshots as the fallback.
- When the Shortcut changes, bump its `X-MistyStep-Shortcut` number, publish a new link, and ship it in the next app release.

**User documentation**

- README: three-step quick start with a short screen recording.
- `TROUBLESHOOTING.md`: "could not connect", "nothing new", Tailscale off on the iPad, PC asleep, the SmartScreen dialog, re-pairing after a rename.
- `PRIVACY.md`: what is stored (settings, token hashes), what is not (clipboard content), and the single optional outbound call.

## Testing plan

Automated tests cover all logic in Core and Server. Clipboard formats, networking and the Shortcut need a manual pass on real devices before each release.

**Core tests (xUnit)**

- Decision order: one test per row of "Expected results by source", plus one per secrecy marker, using a fake `RawClip`.
- Text: CRLF conversion, trailing NUL, whitespace-only, and the size limit at exactly the limit and one byte over.
- `IsUrl`: a single URL is true; a URL with surrounding text or several lines is false.
- Store expiry with a fake clock: just before, at, and after the limit; 0 never expires.
- Generation discard: an older encode finishing after a newer capture must not win.
- Device registry: pair, authenticate, revoke, wrong token. The serialised config must not contain the token.
- Pairing codes: expiry, binding to the first address, cancel-all after six failures.
- Lockout: five failures lock, release after 5 minutes, eviction at 1,000 entries.
- Address policy, table-driven: loopback, 100.64.0.1, 100.127.255.254, 100.128.0.1, LAN inside and outside the subnet, a public address.
- `Host` header validation, setup-code round trip with a non-ASCII PC name, atomic config write, corrupt-config fallback.

**Server tests (TestHost, no real port)**

- Every route: success, missing token, wrong token, wrong method, unknown path returns 404 with an empty body.
- `/v1/meta` for text, image and each `none` reason. With `new=1`, a second call returns `unchanged` while a second device still receives the item.
- `/v1/data`: correct bytes, `Content-Type` and `Content-Length`; 204 when the item is text.
- No `Server` header; `Cache-Control: no-store` on every response.
- A request with a body, or with oversize headers, is rejected.
- Leak check: copy a sentinel string through the whole pipeline, then assert it appears in no log line.

**Manual matrix**

| Area | Cases |
| --- | --- |
| Copy sources | Every row of "Expected results by source" |
| Images | 4K screenshot, transparent PNG from a browser, JPEG file from Explorer, image above 4096 px, animated GIF file |
| Text | Emoji, right-to-left text, exactly 1 MB, 1 MB plus one byte |
| Network | Tailscale at home; Tailscale with the iPad on cellular; Home Wi-Fi on a Private network; the same Wi-Fi marked Public (must refuse); a second device with a wrong token (401, then lockout) |
| Lifecycle | Cold boot with autostart before Tailscale connects, sleep and resume, Wi-Fi switch, Remote Desktop session, locked screen, second instance, port taken |
| Pairing | First device, page reload, second device, expired code, cancel, revoke then fetch |
| Shortcut | Manual run for text, image and nothing new; `auto` automation; `open` with a URL; PC asleep |
| Install | Clean install, upgrade keeping paired devices, uninstall removes firewall rules, portable zip, SmartScreen flow |
| Security | Port scan from another LAN device in Tailscale-only mode, request with a foreign `Host` header, search logs for the sentinel |

Test on Windows 11 x64, an iPad on the current iPadOS and the previous major version, and one iPhone.

## Build order

Build in seven milestones, in order. Each ends with all tests passing and one acceptance check; the first end-to-end paste happens at M4.

| Milestone | Builds | Done when |
| --- | --- | --- |
| M1 Core | Models, clip store with expiry, decision order over fake clips, address policy, device registry, lockout, setup-code codec, config persistence | All Core tests pass and Core has no Windows reference |
| M2 Server | Kestrel host, request pipeline, four routes, limits | Server tests pass; `curl` against a console host returns a hard-coded item with a test token |
| M3 Capture | Win32 watcher and reader, image conversion, a console harness that prints type and size per copy | Every row of "Expected results by source" prints the expected type on a real PC |
| M4 Tray and pairing | Tray icon, settings file, pairing wizard, QR, pairing page, Devices window. A person builds the Shortcut from `BUILD.md` | Text and an image travel from PC to iPad over Tailscale; the M4 items in Open questions are answered |
| M5 Networking and hardening | Tailscale detection, Home Wi-Fi mode, network category, firewall preflight and helper, `Host` check, health status | Network and Security rows of the manual matrix pass |
| M6 Quality of life | Pause, Manual mode and hotkey, expiry options, excluded apps, fetch feedback, troubleshooting window, first-run wizard | Every Version 1.0 feature can be demonstrated; Lifecycle rows pass |
| M7 Packaging | Installer, portable zip, signing, release workflow, user docs, published Shortcut link | Someone who has never seen the app goes from download to first paste in under 3 minutes; Install rows pass |

**Ground rules for the coding model**

- Within a milestone, write Core code and its tests first, then platform code.
- Add no feature, route, setting or dependency that is not in this plan. Raise a question instead.
- `MistyStep.Core` targets plain `net10.0` and must compile without Windows references. All P/Invoke stays in `MistyStep.Windows`.
- Treat every item in Open questions as an assumption. If one proves false, stop and report it; do not work around it silently.
- Three things need a person: building the Shortcut on an iPad, running the manual matrix, and setting up code signing.

## Open questions

These could not be confirmed from documentation and must be checked on real devices. The Shortcut items decide whether parts of the design change, so answer them at M4 before building further.

**Shortcut behaviour (M4)**

- [x] Does "Get Contents of URL" accept plain `http://` to a 100.x address, a MagicDNS name and a `.local` name on current iPadOS? If not, serve HTTPS through Tailscale certificates and drop Home Wi-Fi mode. **Answer (2026-11-14, iPadOS 18):** yes for the 100.x address and for the MagicDNS name (pairing page and Shortcut both worked). `.local` is checked with Home Wi-Fi in M5.
- [x] On a 401 or 429 response, does the Shortcut still receive the JSON body? If not, return 200 with the error JSON. **Answer:** not settled. A revoked device got "Conversion Error: Get Dictionary Value failed because Shortcuts could not convert from Text to Dictionary". The cause was the Shortcut: the `message` lookup read the previous result instead of `Meta`. With that fixed, the alert works over 200. `/v1` errors stay at 200 with the error JSON, which avoids the question; whether a 401 body arrives was not retested. BUILD.md now warns about the lookup.
- [x] Does the Base64 decode action accept the setup code exactly as specified? **Answer:** yes.
- [ ] Does "Copy to Clipboard" on a fetched PNG paste as an image in Notes, Mail and Messages? Repeat for JPEG, GIF and WebP. **Partial:** a copied image pastes on the iPad. JPEG, GIF and WebP files are not yet tested.
- [x] Which permission prompts appear on first run, and does "Always Allow" make later runs silent? **Answer:** a single Allow prompt to connect; later runs are silent.
- [x] What does the user see when the PC is unreachable, in a manual run and in an `auto` automation? How long is the timeout? **Answer:** a manual run shows "Request timed out" after 1 minute (the Shortcuts default; "Get Contents of URL" has no timeout setting). The automation was dropped, see below.
- [x] Is the App Opened trigger with Run Immediately reliable on iPadOS, and is its notification banner tolerable in daily use? **Answer:** it runs, but only when an app opens, so it cannot keep the clipboard current while working. Dropped from the docs. The pairing page and BUILD.md recommend an AssistiveTouch button or a Control Center control instead.
- [ ] Does the pasted setup code sync to the user's iPhone through iCloud? If so, one token serves two devices; decide whether to document or discourage that. **Answer:** not tested; the user has no iPhone. Still open for other users.

**Networking (M5)**

- [ ] Does `<hostname>.local` resolve from an iPad on common home routers? If unreliable, default to the IP address.
- [ ] Which firewall profile does Windows assign to the Tailscale adapter, and does the `profile=any` rule with `remoteip` behave as intended? **Partial (2026-11-14):** on the owner's PC the Tailscale adapter is Private and the Wi-Fi is Public. Windows had auto-created broad "mistystep.exe" allow rules (Private and Public) from its own prompt; the firewall helper replaces them with the two scoped rules. After the helper ran, only the two scoped rules remained for the program, and pairing plus fetching over Tailscale worked through the `profile=any`, `remoteip=100.64.0.0/10` rule.
- [ ] Does Tailscale on iPadOS stay connected in the background well enough that the Shortcut works without opening the Tailscale app?
- [ ] Do Tailscale's current plan terms cover this personal two-device use for free? Check before recommending it in the README.

**Pending M5 manual checks** (owner will run them at home; M5 was merged with these open)

- [ ] Home Wi-Fi on a Private network: set the PC's Wi-Fi to Private, pick Home Wi-Fi in Pair a device, turn Tailscale off on the iPad, and scan. Does the `.local` page open, or only the IP fallback? This also answers the `.local` question above.
- [ ] The same Wi-Fi switched back to Public must refuse: reloading the pairing page on the iPad no longer loads after about 15 s.
- [ ] Tailscale with the iPad on cellular, or on another network such as a phone hotspot.
- [ ] Port scan from another LAN device in Tailscale-only mode. The owner's Wi-Fi is Public, and the Home Wi-Fi rule is off, so connections are expected to fail. From the PC itself, a request to its own Wi-Fi address was dropped.

**Release (M7)**

- [ ] Is the name "Misty Step" free as a trademark, GitHub repository and winget package id?
- [ ] Is the self-contained build size acceptable? If not, add a small framework-dependent build.
- [ ] Which signing option applies in the publisher's country?

**Product decisions for the owner**

- [ ] Is 10 minutes the right default expiry?
- [ ] Should the unencrypted Home Wi-Fi mode ship in 1.0, or wait until HTTPS is available?

## Sources

Opened in full:

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), Microsoft
- [Code signing options for Windows app developers](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options), Microsoft Learn
- [Share shortcuts](https://support.apple.com/guide/shortcuts/apdf01f8c054/ios), Apple Shortcuts User Guide
- [All Automations Now Run Immediately In Shortcuts](https://matthewcassinelli.com/automations-run-immediately-shortcuts-notifications/), Matthew Cassinelli

Read from search excerpts only:

- [Apple iPhone and Windows clipboard sync](https://betanews.com/article/apple-iphone-windows-clipboard-sync-eu/), BetaNews
- [Galaxy Books could access iPhone clipboard items](https://www.sammobile.com/news/galaxy-books-windows-access-iphone-clipboard-items-future/), SamMobile
- [Apple is building iPhone-Windows clipboard sync](https://tbreak.com/iphone-to-windows-clipboard-sync-eu-apple-confirms/), tbreak
- [Apple Shortcuts integration](https://docs.indigodomo.com/2025.2/user/automation/apple-shortcuts/), Indigo documentation
- [Security](https://copyq-de.readthedocs.io/de/stable/security.html), CopyQ documentation
