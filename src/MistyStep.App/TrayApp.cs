using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MistyStep.App.UI;
using MistyStep.Core.Capture;
using MistyStep.Core.Config;
using MistyStep.Core.Models;
using MistyStep.Core.Store;
using MistyStep.Windows.Clipboard;
using MistyStep.Windows.Imaging;

namespace MistyStep.App;

/// <summary>
/// The tray icon and menu, plus the clipboard watcher and capture pipeline (which must live on this UI thread).
/// M4 menu: status, Share current clipboard now, Clear shared item, Pair a device, Devices, Quit.
/// M6 adds Pause, Mode, Settings, Help, Start with Windows and the hotkey.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly AppHost _host;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _status;
    private readonly ClipboardWatcher _watcher;
    private readonly CaptureCoordinator _coordinator;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly SynchronizationContext _ui;
    private PairingForm? _pairing;
    private DevicesForm? _devices;

    public TrayApp(AppHost host, AppOptions options)
    {
        _host = host;

        // Creating the first control installs the WinForms synchronization context on this thread.
        _menu = new ContextMenuStrip();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _status = new ToolStripMenuItem { Enabled = false };
        _menu.Items.Add(_status);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Share current clipboard now", null, (_, _) => _watcher!.RequestRead());
        _menu.Items.Add("Clear shared item", null, (_, _) => ClearSharedItem());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Pair a device...", null, (_, _) => ShowPairing());
        _menu.Items.Add("Devices...", null, (_, _) => ShowDevices());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => ExitThread());
        _menu.Opening += (_, _) => RefreshStatus();

        _icon = new NotifyIcon { Icon = AppIcon.Create(), ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => { if (_host.Registry.Devices.Count == 0) ShowPairing(); else ShowDevices(); };

        _watcher = new ClipboardWatcher(host.LoggerFactory.CreateLogger<ClipboardWatcher>());
        _coordinator = new CaptureCoordinator(_watcher, host.Store, new GdiImageEncoder(),
            () => new CaptureContext(host.State.Paused, host.Persistence.Settings.Mode, HotkeyTriggered: false),
            () => host.CaptureLimits, host.Clock);
        _coordinator.Completed += outcome => _ui.Post(_ => OnCaptured(outcome), null);
        _watcher.Start();
        if (!_watcher.IsListening) _host.Log.LogWarning("Clipboard listener is not registered");

        _timer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _timer.Tick += (_, _) =>
        {
            _host.Persistence.Tick();
            RefreshStatus();
        };
        _timer.Start();

        RefreshStatus();
        ShowStartupNotices(options);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _pairing?.Close();
        _devices?.Close();
        _coordinator.Dispose();
        _watcher.Dispose();
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowStartupNotices(AppOptions options)
    {
        if (_host.ServerError is { } error)
        {
            Balloon("Misty Step needs attention", $"{error}. Nothing can be shared until this is fixed.", ToolTipIcon.Warning);
        }
        else if (_host.LoadResult.Status == ConfigLoadStatus.RecoveredFromCorrupt)
        {
            Balloon("Settings were reset", "The settings file could not be read. Pair your devices again.", ToolTipIcon.Warning);
        }

        if (_host.Registry.Devices.Count == 0 && !options.Minimized) ShowPairing();
    }

    private void OnCaptured(CaptureOutcome outcome)
    {
        switch (outcome)
        {
            case CaptureOutcome.Stored s:
                _host.Log.LogInformation("Shared {Type} from {Source} ({Mime}, {Size} bytes)", s.Type, s.Source, s.Mime, s.Size);
                break;
            case CaptureOutcome.Ignored i:
                _host.Log.LogDebug("Copy not shared: {Reason}", i.Reason);
                if (i.ShowHint) Balloon("Not shared", HintFor(i.Reason), ToolTipIcon.Info);
                break;
            case CaptureOutcome.Failed f:
                _host.Log.LogWarning("Capture failed: {ErrorType}", f.ErrorType);
                break;
        }

        RefreshStatus();
    }

    private string HintFor(IgnoreReason reason)
    {
        var settings = _host.Persistence.Settings;
        return reason switch
        {
            IgnoreReason.TextTooLarge => string.Create(CultureInfo.CurrentCulture, $"That text is larger than {settings.MaxTextBytes / 1024 / 1024.0:0.#} MB."),
            IgnoreReason.ImageTooLarge => string.Create(CultureInfo.CurrentCulture, $"That image is larger than {settings.MaxImageBytes / 1024 / 1024} MB, even after shrinking it."),
            _ => "That copy was not shared.",
        };
    }

    private void RefreshStatus()
    {
        string line;
        if (_host.ServerError is { } error) line = $"Needs attention: {error}";
        else if (!_watcher.IsListening) line = "Needs attention: clipboard listener is not running";
        else line = ShareStatus.Describe(_host.Store.GetCurrent(), _host.State.Paused, _host.Clock.MonotonicMs);

        _status.Text = line;
        var tooltip = "Misty Step\n" + line;
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private void ClearSharedItem()
    {
        _host.Store.Clear();
        _host.Log.LogInformation("Shared item cleared by the user");
        RefreshStatus();
    }

    private void ShowPairing()
    {
        if (_pairing is { IsDisposed: false })
        {
            _pairing.Activate();
            return;
        }

        _pairing = new PairingForm(_host);
        _pairing.FormClosed += (_, _) => _pairing = null;
        _pairing.Show();
        _pairing.Activate();
    }

    private void ShowDevices()
    {
        if (_devices is { IsDisposed: false })
        {
            _devices.Activate();
            return;
        }

        _devices = new DevicesForm(_host);
        _devices.FormClosed += (_, _) => _devices = null;
        _devices.Show();
        _devices.Activate();
    }

    private void Balloon(string title, string text, ToolTipIcon icon) => _icon.ShowBalloonTip(5000, title, text, icon);
}
