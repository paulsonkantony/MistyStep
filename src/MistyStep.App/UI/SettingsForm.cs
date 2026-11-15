using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Config;
using MistyStep.Core.Models;
using MistyStep.Core.Settings;
using MistyStep.Windows.Network;

namespace MistyStep.App.UI;

/// <summary>
/// Settings from the spec's table that a user changes: mode, hotkey, expiry, serve while locked, fetch toast,
/// secrecy markers, excluded apps, connection modes, port and the weekly update check.
/// Limits and the Shortcut link stay in config.json.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly (string Label, int Minutes)[] ExpiryChoices =
        [("2 minutes", 2), ("10 minutes", 10), ("1 hour", 60), ("Until replaced", 0)];

    private readonly AppHost _host;
    private readonly Func<string, string?> _applyHotkey;
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly TextBox _hotkey = new() { Width = 220, ShortcutsEnabled = false };
    private readonly ComboBox _expiry = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox _serveWhileLocked = new() { AutoSize = true, Text = "Keep sharing while the screen is locked" };
    private readonly CheckBox _notifyOnFetch = new() { AutoSize = true, Text = "Show a notification when a device fetches" };
    private readonly CheckBox _honorSecrecy = new() { AutoSize = true, Text = "Skip copies that apps mark as private (password managers)" };
    private readonly TextBox _excluded = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 420, Height = 80, AcceptsReturn = true };
    private readonly CheckBox _tailscale = new() { AutoSize = true, Text = "Tailscale (encrypted, works on any network)" };
    private readonly CheckBox _lan = new() { AutoSize = true, Text = "Home Wi-Fi (not encrypted; only on networks set to Private)" };
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Width = 100 };
    private readonly CheckBox _updateCheck = new() { AutoSize = true, Text = "Check for a new version once a week (asks GitHub; nothing else is sent)" };
    private Hotkey? _capturedHotkey;

    public SettingsForm(AppHost host, Func<string, string?> applyHotkey)
    {
        _host = host;
        _applyHotkey = applyHotkey;
        Text = "Settings - Misty Step";
        Icon = AppIcon.ForWindow();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = SystemFonts.MessageBoxFont!;

        _mode.Items.AddRange(["Automatic: share every copy", "Manual: share only when I press the hotkey"]);
        foreach (var (label, _) in ExpiryChoices) _expiry.Items.Add(label);
        _hotkey.KeyDown += OnHotkeyKeyDown;
        _hotkey.KeyPress += (_, e) => e.Handled = true;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(Group("Sharing",
            Row("Mode:", _mode),
            Row("Hotkey:", _hotkey, Note("Click the box, then press the keys (for example Ctrl+Alt+C).")),
            Row("Stop sharing an item after:", _expiry),
            _serveWhileLocked,
            _notifyOnFetch));
        layout.Controls.Add(Group("Privacy",
            _honorSecrecy,
            Note("Never share copies from these apps (one program name per line):"),
            _excluded));
        layout.Controls.Add(Group("Connections",
            _tailscale,
            _lan,
            Row("Port:", _port, Note("Changing the port means every device must pair again."))));
        layout.Controls.Add(Group("Updates", _updateCheck));

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        Load += (_, _) => LoadValues();
    }

    private void LoadValues()
    {
        var c = _host.Persistence.Settings;
        _mode.SelectedIndex = c.Mode == CaptureMode.Manual ? 1 : 0;
        _capturedHotkey = Hotkey.TryParse(c.Hotkey, out var hotkey) ? hotkey : null;
        _hotkey.Text = _capturedHotkey?.ToString() ?? c.Hotkey;
        var expiry = Array.FindIndex(ExpiryChoices, e => e.Minutes == c.ExpiryMinutes);
        _expiry.SelectedIndex = expiry >= 0 ? expiry : 1;
        _serveWhileLocked.Checked = c.ServeWhileLocked;
        _notifyOnFetch.Checked = c.NotifyOnFetch;
        _honorSecrecy.Checked = c.HonorSecrecyMarkers;
        _excluded.Text = string.Join(Environment.NewLine, c.ExcludedApps);
        _tailscale.Checked = _host.State.TailscaleEnabled;
        _lan.Checked = c.LanEnabled;
        _port.Value = Math.Clamp(c.Port, (int)_port.Minimum, (int)_port.Maximum);
        _updateCheck.Checked = c.UpdateCheck;
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        var modifiers = HotkeyModifiers.None;
        if (e.Control) modifiers |= HotkeyModifiers.Ctrl;
        if (e.Alt) modifiers |= HotkeyModifiers.Alt;
        if (e.Shift) modifiers |= HotkeyModifiers.Shift;

        var key = (int)e.KeyCode;
        if (Hotkey.KeyNameOrNull(key) is null) return; // a modifier on its own: wait for the key
        var candidate = $"{new Hotkey(modifiers, key)}";
        if (Hotkey.TryParse(candidate, out var hotkey))
        {
            _capturedHotkey = hotkey;
            _hotkey.Text = hotkey!.ToString();
        }
        else
        {
            _hotkey.Text = "Add Ctrl or Alt";
            _capturedHotkey = null;
        }
    }

    private async Task SaveAsync()
    {
        var old = _host.Persistence.Settings;
        if (_capturedHotkey is null)
        {
            MessageBox.Show(this, "Choose a hotkey that uses Ctrl or Alt, plus a letter, digit or F-key.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var hotkeyText = _capturedHotkey.ToString();
        if (hotkeyText != old.Hotkey || !Hotkey.TryParse(old.Hotkey, out _))
        {
            if (_applyHotkey(hotkeyText) is { } problem)
            {
                _applyHotkey(old.Hotkey); // put the previous one back
                MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        if (!_tailscale.Checked && !_lan.Checked
            && MessageBox.Show(this, "With both Tailscale and Home Wi-Fi off, no device can connect. Save anyway?", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return;
        }

        var port = (int)_port.Value;
        if (port != old.Port
            && MessageBox.Show(this, string.Create(CultureInfo.CurrentCulture,
                    $"Move to port {port}? Every paired device must pair again, and Windows will ask for permission to update the firewall."),
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return;
        }

        var excluded = _excluded.Lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mode = _mode.SelectedIndex == 1 ? CaptureMode.Manual : CaptureMode.Automatic;
        var expiry = ExpiryChoices[Math.Max(0, _expiry.SelectedIndex)].Minutes;

        _host.ApplySettings(c => c with
        {
            Mode = mode,
            Hotkey = hotkeyText,
            ExpiryMinutes = expiry,
            ServeWhileLocked = _serveWhileLocked.Checked,
            NotifyOnFetch = _notifyOnFetch.Checked,
            HonorSecrecyMarkers = _honorSecrecy.Checked,
            ExcludedApps = excluded,
            TailscaleEnabled = _tailscale.Checked,
            LanEnabled = _lan.Checked,
            UpdateCheck = _updateCheck.Checked,
        });
        _host.Log.LogInformation("Settings saved");
        _host.RefreshInBackground(firewall: true);

        if (port != old.Port)
        {
            Enabled = false;
            await _host.ChangePortAsync(port);
            var result = await FirewallConfigurator.RunElevatedHelperAsync(_host.ExePath, port, CancellationToken.None);
            _host.Log.LogInformation("Firewall helper after port change: {Result}", result);
            _host.RefreshInBackground(firewall: true);
            if (IsDisposed) return;
            Enabled = true;
            if (_host.ServerError is { } error)
                MessageBox.Show(this, $"{error}. Choose another port.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (result != FirewallConfigurator.HelperResult.Done)
                MessageBox.Show(this, "The port changed, but the firewall rules were not updated. Use Fix firewall in Pair a device.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Close();
    }

    private static GroupBox Group(string title, params Control[] controls)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        panel.Controls.AddRange(controls);
        var group = new GroupBox { Text = title, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8), MinimumSize = new Size(460, 0) };
        group.Controls.Add(panel);
        return group;
    }

    private static Control Row(string label, Control input, Control? note = null)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) }, 0, 0);
        row.Controls.Add(input, 1, 0);
        if (note is not null) row.Controls.Add(note, 1, 1);
        return row;
    }

    private static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(420, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 4),
    };
}
