using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Auth;

namespace MistyStep.App.UI;

/// <summary>Paired devices: name, last seen, items fetched, address. Rename and Revoke.</summary>
internal sealed class DevicesForm : Form
{
    private readonly AppHost _host;
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        Dock = DockStyle.Fill,
    };
    private readonly Label _empty = new()
    {
        Text = "No paired devices yet. Choose \"Pair a device...\" in the tray menu.",
        AutoSize = true,
        Dock = DockStyle.Top,
        Padding = new Padding(0, 8, 0, 8),
    };
    private readonly Button _rename = new() { Text = "Rename...", AutoSize = true };
    private readonly Button _revoke = new() { Text = "Revoke...", AutoSize = true };

    public DevicesForm(AppHost host)
    {
        _host = host;
        Text = "Devices - Misty Step";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont!;
        Size = new Size(720, 360);
        MinimumSize = new Size(520, 260);
        Padding = new Padding(12);

        _list.Columns.Add("Name", 200);
        _list.Columns.Add("Last seen", 160);
        _list.Columns.Add("Items fetched", 110, HorizontalAlignment.Right);
        _list.Columns.Add("Address", 220);

        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        CancelButton = close;
        close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(_revoke);
        buttons.Controls.Add(_rename);

        Controls.Add(_list);
        Controls.Add(_empty);
        Controls.Add(buttons);

        _rename.Click += (_, _) => RenameSelected();
        _revoke.Click += (_, _) => RevokeSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => RenameSelected();
        _host.Registry.Changed += OnRegistryChanged;

        Reload();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _host.Registry.Changed -= OnRegistryChanged;
        base.OnFormClosed(e);
    }

    private void OnRegistryChanged(RegistryChange change)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(Reload);
    }

    private void Reload()
    {
        var selected = SelectedDevice()?.Id;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var device in _host.Registry.Devices)
        {
            var item = new ListViewItem(device.Name) { Tag = device };
            item.SubItems.Add(device.LastSeenUtc is { } seen ? seen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "Never");
            item.SubItems.Add(device.FetchCount.ToString("N0", CultureInfo.CurrentCulture));
            item.SubItems.Add(device.BaseUrl);
            _list.Items.Add(item);
            if (device.Id == selected) item.Selected = true;
        }

        _list.EndUpdate();
        _empty.Visible = _list.Items.Count == 0;
        UpdateButtons();
    }

    private Device? SelectedDevice() => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as Device : null;

    private void UpdateButtons()
    {
        var hasSelection = SelectedDevice() is not null;
        _rename.Enabled = hasSelection;
        _revoke.Enabled = hasSelection;
    }

    private void RenameSelected()
    {
        if (SelectedDevice() is not { } device) return;
        var name = PromptForm.Ask(this, "Rename device", "New name:", device.Name, DeviceRegistry.MaxNameLength);
        if (name is null) return;
        if (!_host.Registry.Rename(device.Id, name))
            MessageBox.Show(this, "Enter a name between 1 and 64 characters.", "Rename device", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void RevokeSelected()
    {
        if (SelectedDevice() is not { } device) return;
        var answer = MessageBox.Show(this,
            $"Revoke {device.Name}? It will no longer be able to read what you copy. To use it again, pair it again.",
            "Revoke device", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;
        _host.Registry.Revoke(device.Id);
        _host.Log.LogInformation("Device revoked by the user");
    }
}

/// <summary>A one-line text prompt.</summary>
internal sealed class PromptForm : Form
{
    private readonly TextBox _input = new() { Width = 320 };

    private PromptForm(string title, string label, string initial, int maxLength)
    {
        Text = title;
        Font = SystemFonts.MessageBoxFont!;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _input.Text = initial;
        _input.MaxLength = maxLength;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true };
        layout.Controls.Add(new Label { Text = label, AutoSize = true });
        layout.Controls.Add(_input);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    public static string? Ask(IWin32Window owner, string title, string label, string initial, int maxLength)
    {
        using var form = new PromptForm(title, label, initial, maxLength);
        return form.ShowDialog(owner) == DialogResult.OK ? form._input.Text : null;
    }
}
