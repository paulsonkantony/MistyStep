# Privacy

Misty Step is built so that what you copy stays between your PC and your own devices.

## What is stored on the PC

- **Settings** in `%APPDATA%\MistyStep\config.json` (or beside the exe in portable mode). They include the paired devices, each with a name, the address it was given, when it was last seen and how many items it fetched.
- **Device keys as SHA-256 hashes only.** The real key exists only on the device, inside its Shortcut. The PC holds it in memory for a few minutes during pairing, then forgets it.
- **Logs** in `%LOCALAPPDATA%\MistyStep\logs`, kept for 7 days. They record types, sizes, device names and outcomes, for example "Shared Text (45 bytes)".

## What is never stored or logged

- What you copy. The shared item lives in memory only, until it expires or you copy something else, and is never written to disk.
- Device keys, pairing codes, setup codes, and the `Authorization` header of requests.

## What leaves the PC

- **The shared item, to your paired devices only.** It goes when one of them asks for it with its key, either over Tailscale (encrypted) or over Home Wi-Fi (not encrypted, and only on networks Windows marks as Private).
- **An optional update check.** If you turn it on (first-run window or Settings), Misty Step asks GitHub once a week for the latest release of this project. The request carries no identifier and nothing about you, your PC or your devices; GitHub sees your IP address, as with any web request. Nothing is downloaded or installed.

That is all. There is no telemetry, no analytics, no crash reporting, and no way to send reports.

## Other software involved

- **Tailscale** connects your devices. Its own app talks to Tailscale's servers to set up the connection, but the shared items travel encrypted directly between your devices. See Tailscale's privacy policy.
- **Apple Shortcuts** runs "Paste from PC" on the iPad. If you share your Shortcut, remove your setup code from it first: it contains your device's key.
