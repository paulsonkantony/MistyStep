using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MistyStep.App.UI;
using MistyStep.Core.Capture;
using MistyStep.Core.Config;
using MistyStep.Core.Models;
using MistyStep.Core.Settings;
using MistyStep.Core.Store;
using MistyStep.Windows.Clipboard;
using MistyStep.Windows.Imaging;
using MistyStep.Windows.Shell;

namespace MistyStep.App;

/// <summary>
/// The tray icon and menu, plus the clipboard watcher, capture pipeline and global hotkey (which must live
/// on this UI thread). Everything a user does daily is within two clicks of the tray (spec, Quality of life).
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly AppHost _host;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _shareNow;
    private readonly ToolStripMenuItem _pauseMenu;
    private readonly ToolStripMenuItem _pause5;
    private readonly ToolStripMenuItem _pause60;
    private readonly ToolStripMenuItem _pauseIndefinite;
    private readonly ToolStripMenuItem _resume;
    private readonly ToolStripMenuItem _modeAutomatic;
    private readonly ToolStripMenuItem _modeManual;
    private readonly ToolStripMenuItem _autostart;
    private readonly ToolStripMenuItem _update = new() { Visible = false };
    private readonly UpdateChecker _updateChecker;
    private readonly ClipboardWatcher _watcher;
    private readonly CaptureCoordinator _coordinator;
    private readonly GlobalHotkey _hotkey;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _pauseTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 200 };
    private readonly SynchronizationContext _ui;
    private readonly Icon _normalIcon = AppIcon.ForTray();
    private readonly Icon _attentionIcon = AppIcon.ForTray(TrayIconKind.Attention);
    private readonly Icon _pausedIcon = AppIcon.ForTray(TrayIconKind.Paused);
    private readonly Icon _pulseIcon = AppIcon.ForTray(TrayIconKind.Pulse);
    private readonly HashSet<string> _warnedStale = new(StringComparer.Ordinal);
    private PairingForm? _pairing;
    private DevicesForm? _devices;
    private SettingsForm? _settings;
    private HelpForm? _help;
    private Action? _balloonClick;
    private Icon _stateIcon;
    private int _ticks;
    private int _pulseSteps;

    public TrayApp(AppHost host, AppOptions options)
    {
        _host = host;

        // Creating the first control installs the WinForms synchronization context on this thread.
        _menu = new ContextMenuStrip();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _stateIcon = _normalIcon;

        _status = new ToolStripMenuItem { Enabled = false };
        _shareNow = new ToolStripMenuItem("Share current clipboard now", null, (_, _) => _watcher!.RequestRead());
        _pause5 = new ToolStripMenuItem("5 minutes", null, (_, _) => PauseSharing(TimeSpan.FromMinutes(5)));
        _pause60 = new ToolStripMenuItem("1 hour", null, (_, _) => PauseSharing(TimeSpan.FromHours(1)));
        _pauseIndefinite = new ToolStripMenuItem("Until I resume", null, (_, _) => PauseSharing(null));
        _resume = new ToolStripMenuItem("Resume sharing", null, (_, _) => ResumeSharing());
        _pauseMenu = new ToolStripMenuItem("Pause sharing", null, _pause5, _pause60, _pauseIndefinite, new ToolStripSeparator(), _resume);
        _modeAutomatic = new ToolStripMenuItem("Automatic", null, (_, _) => SetMode(CaptureMode.Automatic));
        _modeManual = new ToolStripMenuItem("Manual (hotkey only)", null, (_, _) => SetMode(CaptureMode.Manual));
        var modeMenu = new ToolStripMenuItem("Mode", null, _modeAutomatic, _modeManual);
        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart());

        _menu.Items.Add(_status);
        _menu.Items.Add(_update);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_shareNow);
        _menu.Items.Add("Clear shared item", null, (_, _) => ClearSharedItem());
        _menu.Items.Add(_pauseMenu);
        _menu.Items.Add(modeMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Pair a device...", null, (_, _) => ShowPairing());
        _menu.Items.Add("Devices...", null, (_, _) => ShowDevices());
        _menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        _menu.Items.Add("Help and troubleshooting", null, (_, _) => ShowHelp());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_autostart);
        _menu.Items.Add("Quit", null, (_, _) => ExitThread());
        _menu.Opening += (_, _) => RefreshMenu();

        _icon = new NotifyIcon { Icon = _normalIcon, ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => { if (_host.Registry.Devices.Count == 0) ShowPairing(); else ShowDevices(); };
        _icon.BalloonTipClicked += (_, _) => { var click = _balloonClick; _balloonClick = null; click?.Invoke(); };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;

        _watcher = new ClipboardWatcher(host.LoggerFactory.CreateLogger<ClipboardWatcher>());
        _coordinator = new CaptureCoordinator(_watcher, host.Store, new GdiImageEncoder(),
            () => new CaptureContext(host.State.Paused, host.Persistence.Settings.Mode, HotkeyTriggered: false),
            () => host.CaptureLimits, host.Clock);
        _coordinator.Completed += outcome => _ui.Post(_ => OnCaptured(outcome), null);
        _watcher.Start();
        if (!_watcher.IsListening) _host.Log.LogWarning("Clipboard listener is not registered");

        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += () => _watcher.RequestRead();

        _timer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _timer.Tick += (_, _) =>
        {
            _host.Persistence.Tick();
            // Windows does not signal a Private/Public switch, so re-probe every tick; the firewall once a minute.
            _host.RefreshInBackground(firewall: ++_ticks % 4 == 0);
            RefreshStatus();
        };
        _timer.Start();
        _pauseTimer.Tick += (_, _) => OnPauseTick();
        _pulseTimer.Tick += (_, _) => OnPulseTick();

        _host.NetworkChanged += OnHostNetworkChanged;
        _host.StateChanged += OnHostStateChanged;
        _host.ProblemOccurred += OnProblemOccurred;
        _host.Registry.Delivered += OnDelivered;
        SystemEvents.SessionEnded += OnSessionEnded;

        _updateChecker = new UpdateChecker(host);
        _updateChecker.UpdateAvailable += release => _ui.Post(_ => ShowUpdate(release), null);

        var hotkeyProblem = ApplyHotkey(_host.Persistence.Settings.Hotkey);
        RefreshStatus();
        ShowStartupNotices(options, hotkeyProblem);
    }

    /// <summary>A newer release exists: a menu line links to its page. Nothing is downloaded.</summary>
    private void ShowUpdate(ReleaseInfo release)
    {
        if (_update.Visible && _update.Tag is ReleaseInfo shown && shown.Version >= release.Version) return;
        _update.Text = $"Update available: {release.Tag}...";
        _update.Tag = release;
        _update.Visible = true;
        _update.Click -= OnUpdateClick;
        _update.Click += OnUpdateClick;
        Balloon("Update available", $"Misty Step {release.Tag} is available. Click to open its release page.", ToolTipIcon.Info, () => OpenRelease(release));
    }

    private void OnUpdateClick(object? sender, EventArgs e)
    {
        if (_update.Tag is ReleaseInfo release) OpenRelease(release);
    }

    private static void OpenRelease(ReleaseInfo release)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>A second copy of the app was started: open this one's menu instead.</summary>
    public void OnActivationRequested() => _ui.Post(_ =>
    {
        var show = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        if (show is not null) show.Invoke(_icon, null);
        else Balloon("Misty Step is already running", "Right-click this icon for the menu.", ToolTipIcon.Info);
    }, null);

    protected override void ExitThreadCore()
    {
        _host.NetworkChanged -= OnHostNetworkChanged;
        _host.StateChanged -= OnHostStateChanged;
        _host.ProblemOccurred -= OnProblemOccurred;
        _host.Registry.Delivered -= OnDelivered;
        SystemEvents.SessionEnded -= OnSessionEnded;
        _timer.Stop();
        _pauseTimer.Stop();
        _pulseTimer.Stop();
        _pairing?.Close();
        _devices?.Close();
        _settings?.Close();
        _help?.Close();
        _updateChecker.Dispose();
        _hotkey.Dispose();
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
            _pauseTimer.Dispose();
            _pulseTimer.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _normalIcon.Dispose();
            _attentionIcon.Dispose();
            _pausedIcon.Dispose();
            _pulseIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowStartupNotices(AppOptions options, string? hotkeyProblem)
    {
        if (_host.ServerError is { } error)
            Balloon("Misty Step needs attention", $"{error}. Nothing can be shared until this is fixed.", ToolTipIcon.Warning);
        else if (_host.LoadResult.Status == ConfigLoadStatus.RecoveredFromCorrupt)
            Balloon("Settings were reset", "The settings file could not be read. Pair your devices again.", ToolTipIcon.Warning);
        else if (hotkeyProblem is not null)
            Balloon("Hotkey not available", hotkeyProblem, ToolTipIcon.Warning, ShowSettings);

        if (options.Minimized) return;
        if (_host.LoadResult.Status == ConfigLoadStatus.Missing) ShowFirstRun();
        else if (_host.Registry.Devices.Count == 0) ShowPairing();
    }

    // --- Capture and hotkey ---

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

    /// <summary>Registers the hotkey from its settings text. Returns a problem to show, or null when it works.</summary>
    public string? ApplyHotkey(string text)
    {
        if (!Hotkey.TryParse(text, out var hotkey))
        {
            _hotkey.Unregister();
            _host.Log.LogWarning("Hotkey setting is not valid");
            return $"\"{text}\" is not a valid hotkey. Choose one in Settings.";
        }

        if (_hotkey.TryRegister(hotkey!))
        {
            _host.Log.LogInformation("Hotkey {Hotkey} registered", hotkey);
            return null;
        }

        _host.Log.LogWarning("Hotkey {Hotkey} is used by another app", hotkey);
        return $"{hotkey} is already used by another app. Choose a different hotkey in Settings.";
    }

    // --- Pause and mode ---

    private void PauseSharing(TimeSpan? duration)
    {
        _host.Pause.Pause(duration);
        _lastPauseMinutes = duration is { } d ? (int)d.TotalMinutes : 0;
        _host.SyncPause();
        _pauseTimer.Start();
        RefreshStatus();
    }

    private void ResumeSharing()
    {
        _host.Pause.Resume();
        _host.SyncPause();
        RefreshStatus();
    }

    private void OnPauseTick()
    {
        _host.SyncPause();
        if (!_host.Pause.Current.IsPaused) _pauseTimer.Stop();
        RefreshStatus();
    }

    private void SetMode(CaptureMode mode)
    {
        if (_host.Persistence.Settings.Mode == mode) return;
        _host.ApplySettings(c => c with { Mode = mode });
        _host.Log.LogInformation("Mode set to {Mode}", mode);
        if (mode == CaptureMode.Manual)
        {
            var hotkey = _hotkey.Current?.ToString() ?? _host.Persistence.Settings.Hotkey;
            Balloon("Manual mode", $"Nothing is shared until you press {hotkey} (or choose Share current clipboard now).", ToolTipIcon.Info);
        }

        RefreshStatus();
    }

    // --- Status, icon, feedback ---

    private void RefreshMenu()
    {
        RefreshStatus();
        var pause = _host.Pause.Current;
        var userPaused = pause.Reason == PauseReason.User;
        _pause5.Checked = userPaused && pause.ResumesAtMs is not null && _lastPauseMinutes == 5;
        _pause60.Checked = userPaused && pause.ResumesAtMs is not null && _lastPauseMinutes == 60;
        _pauseIndefinite.Checked = userPaused && pause.ResumesAtMs is null;
        _resume.Enabled = userPaused;
        _pauseMenu.Checked = userPaused;

        var mode = _host.Persistence.Settings.Mode;
        _modeAutomatic.Checked = mode == CaptureMode.Automatic;
        _modeManual.Checked = mode == CaptureMode.Manual;
        _shareNow.ShortcutKeyDisplayString = _hotkey.Current?.ToString() ?? "";
        _autostart.Checked = Autostart.IsEnabled(_host.ExePath);
    }

    private int _lastPauseMinutes;

    private void RefreshStatus()
    {
        var health = _host.EvaluateHealth(_watcher.IsListening);
        var pause = _host.Pause.Current;
        string line;
        if (!health.IsHealthy) line = $"Needs attention: {health.Problem}";
        else line = ShareStatus.Describe(_host.Store.GetCurrent(), pause, _host.Clock.MonotonicMs);
        if (health.IsHealthy && !pause.IsPaused && _host.Persistence.Settings.Mode == CaptureMode.Manual)
            line += $" (manual: {_hotkey.Current?.ToString() ?? "hotkey"})";

        _status.Text = line;
        _stateIcon = !health.IsHealthy ? _attentionIcon : pause.IsPaused ? _pausedIcon : _normalIcon;
        if (_pulseSteps == 0 && !ReferenceEquals(_icon.Icon, _stateIcon)) _icon.Icon = _stateIcon;
        var tooltip = "Misty Step\n" + line;
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private void OnDelivered(string deviceName, string itemId) => _ui.Post(_ =>
    {
        _pulseSteps = 6; // three on/off blinks
        _pulseTimer.Start();
        if (!_host.Persistence.Settings.NotifyOnFetch) return;

        var current = _host.Store.GetCurrent().Item;
        var what = current?.Id == itemId ? (current.Type == ClipType.Image ? "an image" : current.IsUrl ? "a link" : "text") : "an item";
        Balloon("Fetched", $"{deviceName} fetched {what}.", ToolTipIcon.Info);
    }, null);

    private void OnPulseTick()
    {
        if (_pulseSteps <= 0)
        {
            _pulseTimer.Stop();
            _icon.Icon = _stateIcon;
            return;
        }

        _icon.Icon = _pulseSteps % 2 == 0 ? _pulseIcon : _stateIcon;
        _pulseSteps--;
    }

    private void OnHostNetworkChanged() => _ui.Post(_ =>
    {
        RefreshStatus();
        WarnAboutStaleDevices();
    }, null);

    private void OnHostStateChanged() => _ui.Post(_ =>
    {
        if (_host.Pause.Current.IsPaused) _pauseTimer.Start();
        RefreshStatus();
    }, null);

    private void OnProblemOccurred(string message) => _ui.Post(_ => Balloon("Misty Step", message, ToolTipIcon.Warning), null);

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e) => _ui.Post(_ => ExitThread(), null);

    /// <summary>One tray warning per device whose stored address stopped matching this PC (renamed, new address).</summary>
    private void WarnAboutStaleDevices()
    {
        var stale = _host.StaleDevices();
        _warnedStale.IntersectWith(stale.Select(d => d.Id));
        var fresh = stale.Where(d => !_warnedStale.Contains(d.Id)).ToList();
        if (fresh.Count == 0) return;

        foreach (var device in fresh) _warnedStale.Add(device.Id);
        _host.Log.LogInformation("{Count} paired device(s) use an old address for this PC", fresh.Count);
        var names = string.Join(", ", fresh.Select(d => d.Name));
        Balloon("Pair again", $"{names} uses an old address for this PC (it was renamed, or its address or port changed). Click to pair again.",
            ToolTipIcon.Warning, ShowPairing);
    }

    private void ClearSharedItem()
    {
        _host.Store.Clear();
        _host.Log.LogInformation("Shared item cleared by the user");
        RefreshStatus();
    }

    private void ToggleAutostart()
    {
        var enable = !Autostart.IsEnabled(_host.ExePath);
        if (!Autostart.Set(enable, _host.ExePath, _host.Options.Portable))
            Balloon("Start with Windows", "The setting could not be changed.", ToolTipIcon.Warning);
        else
            _host.Log.LogInformation("Start with Windows {State}", enable ? "on" : "off");
    }

    // --- Windows ---

    private void ShowPairing() => ShowSingle(ref _pairing, () => new PairingForm(_host));

    private void ShowDevices() => ShowSingle(ref _devices, () => new DevicesForm(_host));

    private void ShowSettings() => ShowSingle(ref _settings, () => new SettingsForm(_host, ApplyHotkey));

    private void ShowHelp() => ShowSingle(ref _help, () => new HelpForm(_host, () => _watcher.IsListening));

    private void ShowFirstRun()
    {
        var form = new FirstRunForm(_host);
        form.FormClosed += (_, _) =>
        {
            if (form.DialogResult == DialogResult.OK) ShowPairing();
        };
        form.Show();
        form.Activate();
    }

    private static void ShowSingle<T>(ref T? field, Func<T> create) where T : Form
    {
        if (field is { IsDisposed: false })
        {
            field.Activate();
            return;
        }

        var form = create();
        field = form;
        form.Show();
        form.Activate();
    }

    private void Balloon(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(5000, title, text, icon);
    }
}
