using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Auth;
using MistyStep.Core.Network;
using MistyStep.Windows.Network;

namespace MistyStep.App.UI;

/// <summary>
/// The pairing wizard: pick Tailscale or Home Wi-Fi, preflight checks (server, firewall, address) with a fix
/// for each, a QR code for <c>http://&lt;host&gt;:&lt;port&gt;/pair?c=&lt;code&gt;</c>, and live status.
/// Shows which device opened the page, and "Paired" once it makes its first authenticated call.
/// "Cancel pairing" revokes a device that connected but is not yours.
/// </summary>
internal sealed class PairingForm : Form
{
    private const string TailscaleDownloadUrl = "https://tailscale.com/download";
    private const string NetworkSettingsUrl = "ms-settings:network-status";
    private const int TryIpAfterMs = 60_000;

    private readonly AppHost _host;
    private readonly RadioButton _tailscale = new() { AutoSize = true, Text = "Tailscale (recommended): encrypted, works on any network", Margin = new Padding(0, 4, 0, 0) };
    private readonly Label _tailscaleNote = NoteLabel();
    private readonly LinkLabel _tailscaleLink = new() { AutoSize = true, Text = "Get Tailscale", Visible = false, Margin = new Padding(20, 0, 0, 4) };
    private readonly RadioButton _wifi = new() { AutoSize = true, Text = "Home Wi-Fi: same network only, not encrypted", Margin = new Padding(0, 4, 0, 0) };
    private readonly Label _wifiNote = NoteLabel();
    private readonly LinkLabel _wifiLink = new() { AutoSize = true, Text = "Open network settings", Visible = false, Margin = new Padding(20, 0, 0, 4) };
    private readonly Label _checks = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 8, 0, 4) };
    private readonly Button _fixFirewall = new() { AutoSize = true, Text = "Fix firewall (needs admin)", Visible = false, Margin = new Padding(0, 0, 0, 8) };
    private readonly PictureBox _qr = new() { Size = new Size(280, 280), SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.None, BackColor = Color.White };
    private readonly TextBox _url = new() { ReadOnly = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
    private readonly Label _instructions = new() { AutoSize = true, MaximumSize = new Size(440, 0) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 12, 0, 4), Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Label _tryIpNote = new() { AutoSize = true, MaximumSize = new Size(440, 0), Visible = false, Margin = new Padding(0, 0, 0, 4) };
    private readonly Button _tryIp = new() { AutoSize = true, Text = "Try the IP address instead", Visible = false };
    private readonly Button _primary = new() { AutoSize = true };
    private readonly Button _cancelPairing = new() { AutoSize = true, Text = "Cancel pairing" };
    private readonly Button _retry = new() { AutoSize = true, Text = "Try again", Visible = false };
    private readonly Button _close = new() { AutoSize = true, Text = "Close", DialogResult = DialogResult.Cancel };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private ConnectionMode? _mode;
    private string? _code;
    private string? _host_;
    private long _codeStartedMs;
    private string? _deviceId;
    private bool _lanTurnedOnHere;
    private bool _busy;
    private Action? _primaryAction;

    public PairingForm(AppHost host)
    {
        _host = host;
        Text = "Pair a device - Misty Step";
        Icon = AppIcon.Create();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = SystemFonts.MessageBoxFont!;
        CancelButton = _close;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Width = 460 };
        layout.Controls.Add(new Label
        {
            Text = "Pair an iPad or iPhone",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 14f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        });
        layout.Controls.Add(new Label { Text = "How will the device connect to this PC?", AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
        layout.Controls.Add(_tailscale);
        layout.Controls.Add(_tailscaleNote);
        layout.Controls.Add(_tailscaleLink);
        layout.Controls.Add(_wifi);
        layout.Controls.Add(_wifiNote);
        layout.Controls.Add(_wifiLink);
        layout.Controls.Add(_checks);
        layout.Controls.Add(_fixFirewall);
        layout.Controls.Add(_qr);
        layout.Controls.Add(_url);
        layout.Controls.Add(_instructions);
        layout.Controls.Add(_status);
        layout.Controls.Add(_tryIpNote);
        layout.Controls.Add(_tryIp);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_cancelPairing);
        buttons.Controls.Add(_retry);
        buttons.Controls.Add(_primary);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        _tailscale.CheckedChanged += (_, _) => { if (_tailscale.Checked) SelectMode(ConnectionMode.Tailscale); };
        _wifi.CheckedChanged += (_, _) => { if (_wifi.Checked) SelectMode(ConnectionMode.HomeWifi); };
        _tailscaleLink.LinkClicked += (_, _) => OpenUrl(TailscaleDownloadUrl);
        _wifiLink.LinkClicked += (_, _) => OpenUrl(NetworkSettingsUrl);
        _fixFirewall.Click += async (_, _) => await FixFirewallAsync();
        _tryIp.Click += (_, _) => { if (_mode is { } mode) _ = StartAsync(mode, useIp: true); };
        _primary.Click += (_, _) => _primaryAction?.Invoke();
        _cancelPairing.Click += (_, _) => CancelPairing();
        _retry.Click += async (_, _) => await BeginAsync();
        _close.Click += (_, _) => Close();
        _timer.Tick += (_, _) => UpdateStatus();
        _host.Registry.Changed += OnRegistryChanged;

        ShowCodeArea(false);
        _cancelPairing.Visible = false;
        SetPrimary(null, null);
        _status.Text = "Checking this PC's network...";
        Shown += async (_, _) => await BeginAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _host.Registry.Changed -= OnRegistryChanged;
        WithdrawUnscannedCode();
        _qr.Image?.Dispose();
        base.OnFormClosed(e);
    }

    private static Label NoteLabel() => new() { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(20, 0, 0, 2), ForeColor = SystemColors.GrayText };

    private void OnRegistryChanged(RegistryChange change)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(UpdateStatus);
    }

    /// <summary>Probes the network, shows which modes are available and picks one.</summary>
    private async Task BeginAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _retry.Visible = false;
            await _host.RefreshNetworkAsync(CancellationToken.None);
            if (IsDisposed) return;

            var network = _host.State.Network;
            var hasTailscale = PairingAddress.HasAddress(ConnectionMode.Tailscale, network);
            var hasWifi = PairingAddress.HasAddress(ConnectionMode.HomeWifi, network);

            _tailscale.Enabled = hasTailscale;
            _tailscaleNote.Text = hasTailscale
                ? $"✓ Tailscale is running on this PC ({network.TailscaleAddress}). Turn it on in the device's Tailscale app too."
                : "Not found on this PC. Tailscale links your PC and iPad securely on any network: install it on both and sign in with the same account.";
            _tailscaleLink.Visible = !hasTailscale;

            _wifi.Enabled = hasWifi;
            _wifiNote.Text = hasWifi
                ? "✓ This PC is on a network Windows marks as Private. Anyone on this Wi-Fi who captures traffic could read what is shared."
                : PairingAddress.OnlyPublicLan(network)
                    ? "Your network is set to Public in Windows. Home Wi-Fi works only on a network set to Private (Settings, Network & internet, your Wi-Fi, Network profile type)."
                    : "This PC is not connected to a home network.";
            _wifiLink.Visible = !hasWifi && PairingAddress.OnlyPublicLan(network);

            if (!hasTailscale && !hasWifi)
            {
                _mode = null;
                WithdrawUnscannedCode();
                ShowCodeArea(false);
                _checks.Text = "";
                _fixFirewall.Visible = false;
                _status.Text = "Set up Tailscale, or set your network to Private, then try again.";
                SetPrimary(null, null);
                _cancelPairing.Visible = false;
                _retry.Visible = true;
                return;
            }

            var preferred = _mode is { } current && PairingAddress.HasAddress(current, network)
                ? current
                : hasTailscale ? ConnectionMode.Tailscale : ConnectionMode.HomeWifi;
            _mode = null; // force a fresh start even if the same radio stays checked
            var radio = preferred == ConnectionMode.Tailscale ? _tailscale : _wifi;
            if (radio.Checked) SelectMode(preferred);
            else radio.Checked = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private void SelectMode(ConnectionMode mode)
    {
        if (_mode == mode) return;
        _ = StartAsync(mode, useIp: false);
    }

    /// <summary>Runs the preflight checks for the mode and, if they pass, shows a fresh code.</summary>
    private async Task StartAsync(ConnectionMode mode, bool useIp)
    {
        _mode = mode;
        WithdrawUnscannedCode();
        _code = null;
        _deviceId = null;
        _timer.Stop();
        _tryIp.Visible = false;
        _tryIpNote.Visible = false;

        var state = _host.State;
        var host = PairingAddress.ChooseHost(mode, state.Network, useIp);
        var firewall = await Task.Run(() => _host.RefreshFirewall(mode));
        if (IsDisposed || _mode != mode) return;

        var lines = new List<string>();
        var blocking = false;
        if (_host.ServerError is { } error)
        {
            lines.Add($"✗ {error}. Close other copies of Misty Step or anything else using it.");
            blocking = true;
        }
        else
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"✓ Sharing server is listening on port {state.Port}"));
        }

        switch (firewall)
        {
            case FirewallState.Blocked:
                lines.Add("✗ Windows Firewall has a rule blocking Misty Step (often from cancelling Windows' own prompt). Fix firewall replaces it.");
                break;
            case FirewallState.MissingRule:
                lines.Add("✗ Windows Firewall has no Misty Step rule for this connection, so the device may not get through.");
                break;
            case FirewallState.Unknown:
                lines.Add("? Windows Firewall rules could not be read.");
                break;
            default:
                lines.Add("✓ Windows Firewall lets the device in");
                break;
        }

        if (host is null)
        {
            lines.Add("✗ No address for this connection right now.");
            blocking = true;
        }
        else
        {
            lines.Add($"✓ Address: {host}");
        }

        _checks.Text = string.Join(Environment.NewLine, lines);
        _fixFirewall.Visible = firewall is FirewallState.Blocked or FirewallState.MissingRule;

        if (blocking || host is null)
        {
            ShowCodeArea(false);
            _status.Text = "Fix the problem above, then try again.";
            SetPrimary(null, null);
            _cancelPairing.Visible = false;
            _retry.Visible = true;
            return;
        }

        _retry.Visible = false;
        _cancelPairing.Text = "Cancel pairing";

        if (mode == ConnectionMode.Tailscale)
        {
            _host.EnableTailscale();
        }
        else if (!state.LanEnabled)
        {
            _host.EnableLan();
            _lanTurnedOnHere = true;
        }

        var baseUrl = PairingAddress.BaseUrl(host, state.Port);
        var code = _host.Registry.CreatePairingCode(baseUrl, state.PcName);
        _code = code.Code;
        _host_ = host;
        _codeStartedMs = _host.Clock.MonotonicMs;
        var pairUrl = $"{baseUrl}/pair?c={code.Code}";

        _qr.Image?.Dispose();
        _qr.Image = QrImage.Create(pairUrl);
        _url.Text = pairUrl;
        _instructions.Text =
            (mode == ConnectionMode.Tailscale
                ? "1. Make sure Tailscale is on and signed in on the iPad."
                : "1. Make sure the iPad is on the same Wi-Fi as this PC.") + Environment.NewLine +
            "2. Scan this code with the iPad's Camera app. A page opens in Safari." + Environment.NewLine +
            "3. That page gives you a setup code and walks you through creating a \"Paste from PC\" Shortcut " +
            "in Apple's Shortcuts app (a one-tap action on the iPad that fetches what you copied here)." + Environment.NewLine +
            "4. Run the Shortcut once. This window then shows Paired.";
        ShowCodeArea(true);
        _host.Log.LogInformation("Pairing code created for {Mode} ({HostKind})", mode, PairingAddress.IsIpHost(host) ? "IP address" : "name");
        _timer.Start();
        UpdateStatus();
    }

    private async Task FixFirewallAsync()
    {
        _fixFirewall.Enabled = false;
        try
        {
            var result = await FirewallConfigurator.RunElevatedHelperAsync(_host.ExePath, _host.State.Port, CancellationToken.None);
            _host.Log.LogInformation("Firewall helper: {Result}", result);
            if (IsDisposed) return;

            if (result == FirewallConfigurator.HelperResult.Cancelled)
            {
                MessageBox.Show(this, "The firewall was not changed, because the Windows permission prompt was cancelled.",
                    "Misty Step", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (result == FirewallConfigurator.HelperResult.Failed)
            {
                MessageBox.Show(this, "The firewall rules could not be changed.", "Misty Step", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            await Task.Run(() => _host.RefreshFirewall());
            if (IsDisposed) return;
            if (_mode is { } mode) await RecheckAsync(mode);
        }
        finally
        {
            if (!IsDisposed) _fixFirewall.Enabled = true;
        }
    }

    /// <summary>Re-runs the checks after a firewall fix, keeping the current code if one is showing.</summary>
    private async Task RecheckAsync(ConnectionMode mode)
    {
        if (_code is not null && _host.Registry.GetPairingStatus(_code) is not PairingStatus.Gone)
        {
            var firewall = await Task.Run(() => _host.RefreshFirewall(mode));
            if (IsDisposed) return;
            var lines = _checks.Text.Split(Environment.NewLine).ToList();
            var index = lines.FindIndex(l => l.Contains("Firewall", StringComparison.Ordinal));
            var line = firewall == FirewallState.Ok ? "✓ Windows Firewall lets the device in" : "✗ Windows Firewall still does not let the device in.";
            if (index >= 0) lines[index] = line;
            _checks.Text = string.Join(Environment.NewLine, lines);
            _fixFirewall.Visible = firewall is FirewallState.Blocked or FirewallState.MissingRule;
            return;
        }

        await StartAsync(mode, useIp: false);
    }

    private void UpdateStatus()
    {
        if (_code is null || IsDisposed) return;

        switch (_host.Registry.GetPairingStatus(_code))
        {
            case PairingStatus.Waiting waiting:
                _status.Text = $"Waiting for a device to scan the code (expires in {Remaining(waiting.ExpiresAtMs)}).";
                SetPrimary(null, null);
                _cancelPairing.Visible = false;
                OfferIpAfterAWhile();
                break;

            case PairingStatus.Redeemed redeemed:
                _deviceId = redeemed.Device.Id;
                HideTryIp();
                ShowDevice(redeemed.Device);
                break;

            case PairingStatus.Gone:
                HideTryIp();
                var device = _deviceId is null ? null : _host.Registry.Devices.FirstOrDefault(d => d.Id == _deviceId);
                if (device is not null)
                {
                    ShowDevice(device);
                }
                else
                {
                    _timer.Stop();
                    _status.Text = _deviceId is null ? "This code has expired." : "Pairing was cancelled.";
                    _cancelPairing.Visible = false;
                    _qr.Visible = false;
                    SetPrimary("New code", () => { if (_mode is { } mode) _ = StartAsync(mode, useIp: false); });
                }

                break;
        }
    }

    /// <summary>A name the iPad cannot resolve leaves the page unloaded; after 60 s, offer the plain IP address.</summary>
    private void OfferIpAfterAWhile()
    {
        if (_tryIp.Visible || _host_ is null || PairingAddress.IsIpHost(_host_)) return;
        if (_host.Clock.MonotonicMs - _codeStartedMs < TryIpAfterMs) return;

        _tryIpNote.Text = _mode == ConnectionMode.HomeWifi
            ? $"Nothing yet? The iPad may not find \"{_host_}\". Try this PC's IP address instead. It can change: if pairing stops working later, give this PC a fixed address in your router (a DHCP reservation)."
            : $"Nothing yet? The iPad may not find \"{_host_}\". Try the Tailscale IP address instead.";
        _tryIpNote.Visible = true;
        _tryIp.Visible = true;
    }

    private void HideTryIp()
    {
        _tryIp.Visible = false;
        _tryIpNote.Visible = false;
    }

    private void ShowDevice(Device device)
    {
        if (device.LastSeenUtc is not null)
        {
            _timer.Stop();
            if (_mode == ConnectionMode.HomeWifi) _lanTurnedOnHere = false; // keep Home Wi-Fi on: a device uses it
            _status.Text = $"✓ Paired with {device.Name}. Run \"Paste from PC\" on the device any time.";
            _cancelPairing.Visible = false;
            _qr.Visible = false;
            _close.Text = "Done";
            SetPrimary("Pair another", () => { if (_mode is { } mode) _ = StartAsync(mode, useIp: false); });
        }
        else
        {
            _status.Text = $"{device.Name} opened the pairing page. Follow the steps on that page: copy the setup code, " +
                           "create the \"Paste from PC\" Shortcut in the Shortcuts app with the code inside it, then run it once. " +
                           "This window shows Paired when that first run reaches this PC.";
            _cancelPairing.Text = "Cancel pairing (not my device)";
            _cancelPairing.Visible = true;
            SetPrimary(null, null);
        }
    }

    private void CancelPairing()
    {
        if (_code is not null) _host.Registry.CancelPairing(_code);
        if (_deviceId is not null) _host.Registry.Revoke(_deviceId);
        _host.Log.LogInformation("Pairing cancelled by the user");
        _code = null;
        Close();
    }

    /// <summary>
    /// Withdraws a code nobody scanned. A device that already opened the page keeps its pairing.
    /// Home Wi-Fi mode turned on only for this attempt is turned off again if no device is using it.
    /// </summary>
    private void WithdrawUnscannedCode()
    {
        if (_code is not null && _host.Registry.GetPairingStatus(_code) is PairingStatus.Waiting)
            _host.Registry.CancelPairing(_code);

        if (_lanTurnedOnHere && !_host.Registry.Devices.Any(d => IsLanDevice(d.BaseUrl)))
        {
            _host.State.LanEnabled = false;
            _host.Persistence.Update(c => c with { LanEnabled = false });
            _host.Log.LogInformation("Home Wi-Fi mode turned off again: no device paired with it");
        }

        _lanTurnedOnHere = false;
    }

    private static bool IsLanDevice(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(uri.Host, out var ip) && !AddressPolicy.TailscaleRange.Contains(ip)));

    private void ShowCodeArea(bool visible)
    {
        _qr.Visible = visible;
        _url.Visible = visible;
        _instructions.Visible = visible;
        _close.Text = "Close";
        if (!visible) HideTryIp();
    }

    private void SetPrimary(string? text, Action? action)
    {
        _primaryAction = action;
        _primary.Visible = text is not null;
        if (text is not null) _primary.Text = text;
    }

    private string Remaining(long expiresAtMs)
    {
        var seconds = Math.Max(0, (expiresAtMs - _host.Clock.MonotonicMs) / 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
