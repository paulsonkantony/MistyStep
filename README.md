# Misty Step

Misty Step brings what you copy on your Windows PC to your iPad or iPhone. A small tray app on the PC shares the latest text or image you copied. A "Paste from PC" Shortcut on the iPad fetches it with one tap, over Tailscale or your home Wi-Fi.

## About this project

- **Built entirely with AI.** Every line of code and documentation was written with an AI assistant (Claude Code). I directed the work and tested it on my own PC and iPad.
- **Made for my personal use.** I'm sharing it in case others find it useful.
- **Provided as is.** There is no warranty and no promise of support, fixes or updates. I take no responsibility for any problem it causes. Use it at your own risk.
- **No reports.** The app sends nothing to me or anyone else, and there is no way to submit reports.

## Quick start

1. **Install.** Download `MistyStep-<version>-x64-setup.exe` from [Releases](https://github.com/paulsonkantony/MistyStep/releases) (`arm64` for Windows on Arm) and run it. Install [Tailscale](https://tailscale.com/download) on the PC and the iPad, and sign in to both with the same account.
2. **Pair.** Misty Step opens a welcome window, then **Pair a device**. Scan the QR code with the iPad's Camera app. A page opens in Safari with a setup code.
3. **Create the Shortcut.** Follow the steps on that page (also in [`shortcut/BUILD.md`](shortcut/BUILD.md)) to build "Paste from PC" in Apple's Shortcuts app, with the setup code inside it. It takes about 15 minutes, once. Run it once and choose **Always Allow**.

From then on: copy on the PC, run "Paste from PC" on the iPad, and paste. For one tap from inside any app, add the Shortcut to the AssistiveTouch button or Control Center (see the pairing page).

## Downloads and the Windows warning

Each release has an installer (`-setup.exe`, recommended) and a portable zip (`-portable.zip`, runs from any folder and keeps its settings there). Both bundle .NET, so nothing else is needed. They are about 55 MB.

The files are not code-signed, so Windows may show **"Windows protected your PC"**. Choose **More info**, then **Run anyway**. To check that a download is the real one, run this in PowerShell and compare the result with `SHA256SUMS.txt` on the release page:

```powershell
Get-FileHash .\MistyStep-<version>-x64-setup.exe
```

Requirements: Windows 11 (Windows 10 22H2 should work), an iPad or iPhone with the Shortcuts app, and Tailscale on both. Home Wi-Fi mode works without Tailscale, but only on a network Windows marks as Private, and it is not encrypted.

## Privacy

Misty Step keeps everything on your PC and your own devices. It stores settings and hashes of device keys, never what you copy. It sends no telemetry and no reports. Its only internet request is an optional weekly check for a new version on GitHub, off unless you turn it on. Details: [`docs/PRIVACY.md`](docs/PRIVACY.md).

## Troubleshooting

See [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md), and **Help and troubleshooting** in the tray menu, which runs the health checks and shows diagnostic details.

## Building from source

Needs the .NET 10 SDK, and [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

```powershell
dotnet test --solution MistyStep.sln                                     # build and run all tests
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1          # installers, zips and checksums in artifacts/
```

Pushing a tag such as `v1.0.0` runs the same build on GitHub and creates a draft release. The design and build plan are in [`plan/`](plan/).

## Credits

**App icon:** ["Wingfoot"](https://game-icons.net/1x1/lorc/wingfoot.html) by [Lorc](https://lorcblog.blogspot.com), from [game-icons.net](https://game-icons.net), licensed under [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/). Changes: the white artwork is placed on a blue rounded square and resized for Windows icons. The original files are in [`icon/`](icon/), and [`tools/make-icon.ps1`](tools/make-icon.ps1) builds the app's `.ico` from them.

**QR codes:** [QRCoder](https://github.com/codebude/QRCoder), MIT License. See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

Misty Step is released under the [MIT License](LICENSE).
