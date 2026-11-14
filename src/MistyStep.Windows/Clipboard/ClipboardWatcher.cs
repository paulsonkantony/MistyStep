using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Capture;
using static MistyStep.Windows.Clipboard.NativeMethods;
using FormsTimer = System.Windows.Forms.Timer;

namespace MistyStep.Windows.Clipboard;

/// <summary>
/// Watches the Windows clipboard from a hidden message-only window on the UI thread.
/// <list type="bullet">
/// <item>Subscribes with AddClipboardFormatListener and debounces WM_CLIPBOARDUPDATE by 150 ms.</item>
/// <item>Skips a read when GetClipboardSequenceNumber has not moved (except for hotkey reads).</item>
/// <item>Every 30 s, if the sequence number moved without a notification, reads now and re-registers.</item>
/// <item>Opens the clipboard with retries (without blocking the message loop) and closes it in a finally block.</item>
/// </list>
/// Create, start and use it on the STA UI thread that runs the message loop. It never writes to the clipboard.
/// </summary>
public sealed class ClipboardWatcher : IClipboardSource, IDisposable
{
    public static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    /// <summary>Waits between OpenClipboard attempts: 25, 50, then 100 ms, up to 8 retries.</summary>
    internal static readonly int[] RetryDelaysMs = [25, 50, 100, 100, 100, 100, 100, 100];

    private readonly ILogger _logger;
    private readonly ListenerWindow _window;
    private readonly FormsTimer _debounce;
    private readonly FormsTimer _watchdog;
    private uint _lastSequence;
    private bool _reading;
    private ReadTrigger? _pending;
    private bool _started;
    private bool _disposed;

    public ClipboardWatcher(ILogger<ClipboardWatcher> logger)
    {
        _logger = logger;
        _window = new ListenerWindow(this);
        _debounce = new FormsTimer { Interval = (int)DebounceInterval.TotalMilliseconds };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = ReadAsync(ReadTrigger.ClipboardChange);
        };
        _watchdog = new FormsTimer { Interval = (int)WatchdogInterval.TotalMilliseconds };
        _watchdog.Tick += (_, _) => OnWatchdog();
    }

    public event Action<RawClip, ReadTrigger>? Captured;

    /// <summary>True while the clipboard listener is registered (one of the four health checks).</summary>
    public bool IsListening { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;

        _window.CreateHandle(new CreateParams { Caption = "MistyStep.ClipboardWatcher", Parent = HWND_MESSAGE });
        // Start from the current state: copies made before launch are not shared.
        _lastSequence = GetClipboardSequenceNumber();
        Register();
        _watchdog.Start();
    }

    /// <summary>Reads the clipboard now regardless of the sequence number ("Share current clipboard now" / hotkey).</summary>
    public void RequestRead() => _ = ReadAsync(ReadTrigger.Hotkey);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Dispose();
        _watchdog.Dispose();
        if (_window.Handle != 0)
        {
            RemoveClipboardFormatListener(_window.Handle);
            _window.DestroyHandle();
        }

        IsListening = false;
    }

    private void Register()
    {
        IsListening = AddClipboardFormatListener(_window.Handle);
        if (!IsListening)
            _logger.LogWarning("Clipboard listener registration failed (error {Error})", System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
    }

    private void OnClipboardUpdate()
    {
        // Restart the debounce: Office and Excel raise several updates per copy.
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnWatchdog()
    {
        if (_reading || _debounce.Enabled) return;
        if (GetClipboardSequenceNumber() == _lastSequence && IsListening) return;

        _logger.LogInformation("Clipboard changed without a notification; re-registering the listener");
        RemoveClipboardFormatListener(_window.Handle);
        Register();
        _ = ReadAsync(ReadTrigger.Watchdog);
    }

    private async Task ReadAsync(ReadTrigger trigger)
    {
        if (_disposed) return;
        if (_reading)
        {
            // Another read is waiting for the clipboard; run once more when it finishes.
            _pending = trigger == ReadTrigger.Hotkey || _pending == ReadTrigger.Hotkey ? ReadTrigger.Hotkey : trigger;
            return;
        }

        _reading = true;
        try
        {
            if (trigger != ReadTrigger.Hotkey && GetClipboardSequenceNumber() == _lastSequence) return;
            if (!await OpenWithRetriesAsync()) return;

            Win32RawClip? clip = null;
            try
            {
                var sequence = GetClipboardSequenceNumber();
                if (trigger != ReadTrigger.Hotkey && sequence == _lastSequence) return;
                _lastSequence = sequence;

                clip = Win32RawClip.ListFormats();
                Captured?.Invoke(clip, trigger);
            }
            catch (Exception ex)
            {
                // Never include content: log the exception type only.
                _logger.LogWarning("Clipboard read failed: {ErrorType}", ex.GetType().Name);
            }
            finally
            {
                clip?.Invalidate();
                CloseClipboard();
            }
        }
        finally
        {
            _reading = false;
            if (_pending is { } next && !_disposed)
            {
                _pending = null;
                _ = ReadAsync(next);
            }
        }
    }

    private async Task<bool> OpenWithRetriesAsync()
    {
        if (OpenClipboard(_window.Handle)) return true;
        foreach (var delay in RetryDelaysMs)
        {
            // Resumes on the UI thread (WinForms synchronization context), so the message loop keeps running.
            await Task.Delay(delay);
            if (_disposed) return false;
            if (OpenClipboard(_window.Handle)) return true;
        }

        _logger.LogDebug("Clipboard is held by another app; skipping this read");
        return false;
    }

    private sealed class ListenerWindow(ClipboardWatcher owner) : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                owner.OnClipboardUpdate();
                m.Result = 0;
                return;
            }

            base.WndProc(ref m);
        }
    }
}
