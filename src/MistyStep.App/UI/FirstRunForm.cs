using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MistyStep.Windows.Shell;

namespace MistyStep.App.UI;

/// <summary>
/// First run: what Misty Step does, Start with Windows, the update-check question, then straight into pairing
/// (spec, Quality of life). Either button saves the two choices; "Continue" also opens Pair a device.
/// </summary>
internal sealed class FirstRunForm : Form
{
    private readonly AppHost _host;
    private readonly CheckBox _autostart = new() { AutoSize = true, Text = "Start Misty Step when I sign in to Windows", Checked = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly CheckBox _updates = new() { AutoSize = true, Text = "Check for a new version once a week", Margin = new Padding(0, 4, 0, 0) };

    public FirstRunForm(AppHost host)
    {
        _host = host;
        Text = "Welcome - Misty Step";
        Icon = AppIcon.ForWindow();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = SystemFonts.MessageBoxFont!;

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Welcome to Misty Step", AutoSize = true, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold) });
        layout.Controls.Add(Text_(
            "Misty Step shares the last text or image you copy on this PC with your iPad or iPhone. " +
            "On the iPad, one tap on a \"Paste from PC\" Shortcut puts it on the iPad's clipboard, ready to paste."));
        layout.Controls.Add(Text_(
            "It lives in the notification area (bottom right, next to the clock). Right-click its icon for the menu. " +
            "Nothing is stored on disk except settings, and only paired devices can fetch anything."));
        layout.Controls.Add(_autostart);
        layout.Controls.Add(_updates);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 0, 8),
            Text = "Asks GitHub whether a newer release exists. Nothing else is sent. You can change both later.",
        });

        var next = new Button { Text = "Continue to pairing", AutoSize = true, DialogResult = DialogResult.OK };
        var later = new Button { Text = "Not now", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = next;
        CancelButton = later;
        next.Click += (_, _) => Close();
        later.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, MinimumSize = new Size(440, 0) };
        buttons.Controls.Add(later);
        buttons.Controls.Add(next);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (!Autostart.Set(_autostart.Checked, _host.ExePath, _host.Options.Portable))
            _host.Log.LogWarning("Could not change Start with Windows");
        _host.ApplySettings(c => c with { UpdateCheck = _updates.Checked });
        _host.Log.LogInformation("First run finished (start with Windows {Autostart}, update check {Updates})",
            _autostart.Checked ? "on" : "off", _updates.Checked ? "on" : "off");
        base.OnFormClosed(e);
    }

    private static Label Text_(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 8, 0, 0) };
}
