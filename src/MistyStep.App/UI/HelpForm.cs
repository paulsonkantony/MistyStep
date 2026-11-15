using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Network;
using MistyStep.Core.Store;
using MistyStep.Windows.Network;

namespace MistyStep.App.UI;

/// <summary>
/// Help and troubleshooting: the four health checks, addresses, port and firewall state, the log folder,
/// a few tips, and the About section with version and credits. Everything stays on this PC: there is no
/// way to send a report anywhere (owner decision, 2026-11-14).
/// </summary>
internal sealed class HelpForm : Form
{
    private readonly AppHost _host;
    private readonly Func<bool> _listenerRegistered;
    private readonly Label _checks = new() { AutoSize = true, MaximumSize = new Size(500, 0) };
    private readonly Label _connection = new() { AutoSize = true, MaximumSize = new Size(500, 0) };
    private readonly Button _fixFirewall = new() { AutoSize = true, Text = "Fix firewall (needs admin)", Visible = false };

    public HelpForm(AppHost host, Func<bool> listenerRegistered)
    {
        _host = host;
        _listenerRegistered = listenerRegistered;
        Text = "Help and troubleshooting - Misty Step";
        Icon = AppIcon.ForWindow();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = SystemFonts.MessageBoxFont!;

        var refresh = new Button { Text = "Check again", AutoSize = true };
        var logs = new Button { Text = "Open log folder", AutoSize = true };
        refresh.Click += async (_, _) => await RefreshAsync();
        logs.Click += (_, _) => Open(_host.Paths.LogDirectory);
        _fixFirewall.Click += async (_, _) => await FixFirewallAsync();

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        actions.Controls.AddRange([refresh, _fixFirewall, logs]);

        var tips = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Text =
                "• \"Nothing new on PC\": copy the item again on the PC, then run the Shortcut. Items stop being shared after the time set in Settings." + Environment.NewLine +
                "• \"Request timed out\": the PC is asleep or off, or Tailscale is off on the PC or the iPad." + Environment.NewLine +
                "• \"This device is not paired\": pair again from Pair a device, and paste the new setup code into the Shortcut's first Text action.",
        };

        var about = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        about.Controls.Add(new Label { AutoSize = true, Text = $"Misty Step {_host.Version}", Font = new Font(Font, FontStyle.Bold) });
        about.Controls.Add(Link("App icon: \"Wingfoot\" by Lorc, game-icons.net, CC BY 3.0 (recoloured and resized)",
            ("\"Wingfoot\"", "https://game-icons.net/1x1/lorc/wingfoot.html"),
            ("Lorc", "https://lorcblog.blogspot.com"),
            ("game-icons.net", "https://game-icons.net"),
            ("CC BY 3.0", "https://creativecommons.org/licenses/by/3.0/")));
        about.Controls.Add(Link("QR codes: QRCoder, MIT License", ("QRCoder", "https://github.com/codebude/QRCoder")));

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(Group("Health", _checks));
        layout.Controls.Add(Group("Connection", _connection));
        layout.Controls.Add(actions);
        layout.Controls.Add(Group("Tips", tips));
        layout.Controls.Add(Group("About", about));

        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        CancelButton = close;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(close);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        Shown += async (_, _) => await RefreshAsync();
        ShowValues();
    }

    private async Task RefreshAsync()
    {
        _checks.Text = "Checking...";
        await _host.RefreshNetworkAsync(CancellationToken.None);
        await Task.Run(() => _host.RefreshFirewall());
        if (!IsDisposed) ShowValues();
    }

    private void ShowValues()
    {
        var health = _host.EvaluateHealth(_listenerRegistered());
        var lines = new List<string>();
        foreach (var check in Enum.GetValues<HealthCheck>())
        {
            var mark = health.Failing == check ? "✗" : health.Failing is null || check < health.Failing ? "✓" : "–";
            var detail = health.Failing == check ? $": {health.Problem}" : "";
            lines.Add($"{mark} {Health.CheckName(check)}{detail}");
        }

        _checks.Text = string.Join(Environment.NewLine, lines);

        var state = _host.State;
        var network = state.Network;
        var lan = PairingAddress.PrivateLanInterfaces(network).Select(i => i.Address.ToString()).ToList();
        _connection.Text = string.Join(Environment.NewLine,
            string.Create(CultureInfo.CurrentCulture, $"Port: {state.Port}"),
            $"Tailscale: {(state.TailscaleEnabled ? "on" : "off")}, address {network.TailscaleAddress?.ToString() ?? "not found"}" +
            (network.MagicDnsName is { } name ? $", name {name}" : ""),
            $"Home Wi-Fi: {(state.LanEnabled ? "on" : "off")}, " +
            (lan.Count > 0 ? $"{network.Hostname}.local, {string.Join(", ", lan)}" : "no network set to Private"),
            $"Windows Firewall: {FirewallText(_host.Firewall)}",
            $"Sharing: {ShareStatus.Describe(_host.Store.GetCurrent(), _host.Pause.Current, _host.Clock.MonotonicMs)}");
        _fixFirewall.Visible = _host.Firewall is FirewallState.MissingRule or FirewallState.Blocked;
    }

    private static string FirewallText(FirewallState state) => state switch
    {
        FirewallState.Ok => "lets paired devices in",
        FirewallState.MissingRule => "Misty Step rule missing",
        FirewallState.Blocked => "a rule blocks Misty Step",
        _ => "could not be read",
    };

    private async Task FixFirewallAsync()
    {
        _fixFirewall.Enabled = false;
        var result = await FirewallConfigurator.RunElevatedHelperAsync(_host.ExePath, _host.State.Port, CancellationToken.None);
        _host.Log.LogInformation("Firewall helper: {Result}", result);
        if (IsDisposed) return;
        _fixFirewall.Enabled = true;
        await RefreshAsync();
    }

    private static GroupBox Group(string title, Control content)
    {
        content.Dock = DockStyle.Fill;
        var group = new GroupBox { Text = title, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8), MinimumSize = new Size(520, 0) };
        group.Controls.Add(content);
        return group;
    }

    private static LinkLabel Link(string text, params (string Part, string Url)[] links)
    {
        var label = new LinkLabel { AutoSize = true, Text = text, MaximumSize = new Size(500, 0) };
        foreach (var (part, url) in links)
        {
            var start = text.IndexOf(part, StringComparison.Ordinal);
            if (start >= 0) label.Links.Add(start, part.Length, url);
        }

        label.LinkClicked += (_, e) => { if (e.Link?.LinkData is string url) Open(url); };
        return label;
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
