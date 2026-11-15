using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Auth;

namespace MistyStep.App.UI;

/// <summary>
/// The pairing wizard: preflight checks, a QR code for <c>http://&lt;host&gt;:&lt;port&gt;/pair?c=&lt;code&gt;</c>,
/// and live status. Shows which device opened the page, and "Paired" once it makes its first
/// authenticated call. "Cancel pairing" revokes a device that connected but is not yours.
/// M4 pairs over Tailscale only; M5 adds Home Wi-Fi mode and the firewall check.
/// </summary>
internal sealed class PairingForm : Form
{
    private const string TailscaleDownloadUrl = "https://tailscale.com/download";

    private readonly AppHost _host;
    private readonly Label _checks = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 8) };
    private readonly PictureBox _qr = new() { Size = new Size(280, 280), SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.None, BackColor = Color.White };
    private readonly TextBox _url = new() { ReadOnly = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
    private readonly Label _instructions = new() { AutoSize = true, MaximumSize = new Size(440, 0) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 12, 0, 8), Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Button _primary = new() { AutoSize = true };
    private readonly Button _cancelPairing = new() { AutoSize = true, Text = "Cancel pairing" };
    private readonly Button _retry = new() { AutoSize = true, Text = "Try again", Visible = false };
    private readonly Button _close = new() { AutoSize = true, Text = "Close", DialogResult = DialogResult.Cancel };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private string? _code;
    private string? _deviceId;
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
        layout.Controls.Add(_checks);
        layout.Controls.Add(_qr);
        layout.Controls.Add(_url);
        layout.Controls.Add(_instructions);
        layout.Controls.Add(_status);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_cancelPairing);
        buttons.Controls.Add(_retry);
        buttons.Controls.Add(_primary);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        _primary.Click += (_, _) => _primaryAction?.Invoke();
        _cancelPairing.Click += (_, _) => CancelPairing();
        _retry.Click += (_, _) => Begin();
        _close.Click += (_, _) => Close();
        _timer.Tick += (_, _) => UpdateStatus();
        _host.Registry.Changed += OnRegistryChanged;

        Begin();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _host.Registry.Changed -= OnRegistryChanged;
        // A code nobody scanned is withdrawn. A device that already opened the page keeps its pairing.
        if (_code is not null && _host.Registry.GetPairingStatus(_code) is PairingStatus.Waiting)
            _host.Registry.CancelPairing(_code);
        _qr.Image?.Dispose();
        base.OnFormClosed(e);
    }

    private void OnRegistryChanged(RegistryChange change)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(UpdateStatus);
    }

    /// <summary>Runs the preflight checks and, if they pass, shows a fresh code.</summary>
    private void Begin()
    {
        _host.RefreshNetwork();
        _code = null;
        _deviceId = null;

        var problems = new List<string>();
        var lines = new List<string>();
        var state = _host.State;

        if (_host.ServerError is { } error) problems.Add($"✗ {error}. Close other copies of Misty Step or anything else using it.");
        else lines.Add(string.Create(CultureInfo.InvariantCulture, $"✓ Sharing server is listening on port {state.Port}"));

        var tailscale = state.Network.TailscaleAddress;
        if (tailscale is null)
            problems.Add("✗ Tailscale was not found on this PC. Tailscale links your PC and iPad securely on any network. Install it on both and sign in with the same account.");
        else
            lines.Add($"✓ Tailscale address {tailscale}");

        _checks.Text = string.Join(Environment.NewLine, lines.Concat(problems));

        if (problems.Count > 0)
        {
            ShowCodeArea(false);
            _status.Text = "Fix the problem above, then try again.";
            SetPrimary(tailscale is null ? "Get Tailscale" : null, () => OpenUrl(TailscaleDownloadUrl));
            _cancelPairing.Visible = false;
            _retry.Visible = true;
            return;
        }

        _retry.Visible = false;
        _cancelPairing.Text = "Cancel pairing";

        // Pairing over Tailscale turns Tailscale mode on for good.
        state.TailscaleEnabled = true;
        if (_host.Persistence.Settings.TailscaleEnabled != true)
            _host.Persistence.Update(c => c with { TailscaleEnabled = true });

        var baseUrl = string.Create(CultureInfo.InvariantCulture, $"http://{tailscale}:{state.Port}");
        var code = _host.Registry.CreatePairingCode(baseUrl, state.PcName);
        _code = code.Code;
        var pairUrl = $"{baseUrl}/pair?c={code.Code}";

        _qr.Image?.Dispose();
        _qr.Image = QrImage.Create(pairUrl);
        _url.Text = pairUrl;
        _instructions.Text = "Scan the code with the Camera app on your iPad, then follow the steps that open in Safari. " +
                             "Tailscale must be on and signed in on the iPad.";
        ShowCodeArea(true);
        _host.Log.LogInformation("Pairing code created");
        _timer.Start();
        UpdateStatus();
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
                break;

            case PairingStatus.Redeemed redeemed:
                _deviceId = redeemed.Device.Id;
                ShowDevice(redeemed.Device);
                break;

            case PairingStatus.Gone:
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
                    SetPrimary("New code", Begin);
                }

                break;
        }
    }

    private void ShowDevice(Device device)
    {
        if (device.LastSeenUtc is not null)
        {
            _timer.Stop();
            _status.Text = $"✓ Paired with {device.Name}. Run \"Paste from PC\" on the device any time.";
            _cancelPairing.Visible = false;
            _qr.Visible = false;
            _close.Text = "Done";
            SetPrimary("Pair another", Begin);
        }
        else
        {
            _status.Text = $"{device.Name} opened the pairing page. On the device: copy the setup code, add the Shortcut, " +
                           "and paste the code when asked. This window shows Paired when the Shortcut first runs.";
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

    private void ShowCodeArea(bool visible)
    {
        _qr.Visible = visible;
        _url.Visible = visible;
        _instructions.Visible = visible;
        _close.Text = "Close";
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
