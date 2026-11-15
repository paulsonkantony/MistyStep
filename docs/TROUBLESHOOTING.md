# Troubleshooting

Start with **Help and troubleshooting** in the tray menu. It runs the four health checks (clipboard listener, sharing server, network address, Windows Firewall) and shows diagnostic details. When something is wrong, the tray icon gets an orange dot and the menu's first line names the problem.

## "Request timed out" on the iPad (after about a minute)

The iPad could not reach the PC. Check, in order:

1. The PC is on and awake. Misty Step can't answer from a sleeping PC.
2. Tailscale is on and connected on **both** the PC and the iPad, signed in with the same account. Open the Tailscale app on the iPad to check.
3. Misty Step is running on the PC (its icon is in the notification area, next to the clock).
4. In Home Wi-Fi mode: both are on the same Wi-Fi, and Windows marks that network as Private.

Shortcuts always waits about a minute before giving up; this can't be shortened.

## "Nothing new on PC"

The PC has nothing to share right now:

- Nothing was copied since Misty Step started, or the item expired. Items stop being shared after 10 minutes by default; change it in **Settings**.
- Sharing is paused (grey tray icon). Choose **Pause sharing > Resume sharing**.
- **Manual** mode is on: copies are shared only when you press the hotkey (Ctrl+Alt+C by default).
- The copy was skipped on purpose: it came from a password manager, or an app marked it private, or it was too large (a notification says so).

## "This device is not paired"

The device's key was revoked, or the PC's settings were reset. Pair again from **Pair a device**, then open the Shortcut and replace the setup code in its first **Text** action with the new one.

## "Conversion Error: ... could not convert from Text to Dictionary"

A **Get Dictionary Value** action in the Shortcut reads the wrong input. Each one must read from *Meta* (or *Config* for steps 7 and 8). Shortcuts fills in the previous action's result by default, so tap the blue word after "in" and choose *Meta*. The error block (step 14 in `shortcut/BUILD.md`) is the usual culprit.

## The pairing page does not open after scanning

- Wait a minute: the pairing window then offers **Try the IP address instead**. Scan the new QR code.
- Check **Windows Firewall** in the pairing window's checks. If it shows ✗, click **Fix firewall** and accept the Windows prompt.
- Tailscale must be on on the iPad (or, for Home Wi-Fi, the iPad must be on the same Wi-Fi).

## "Windows protected your PC" when installing

The downloads are not code-signed. Choose **More info**, then **Run anyway**. To make sure the file is genuine, compare `Get-FileHash <file>` in PowerShell with `SHA256SUMS.txt` on the release page.

## After renaming the PC, changing its Tailscale name, or changing the port

The setup code inside the Shortcut holds the old address, and the tray shows **Pair again**. Pair again from **Pair a device** and paste the new setup code into the Shortcut's first **Text** action.

## Port 45654 is already in use

Another program, or a second Windows user running Misty Step, has the port. Close it, or pick another port in **Settings** (devices must then pair again).

## Logs

**Help and troubleshooting > Open log folder** opens them. They never contain what you copied, device keys or setup codes.
