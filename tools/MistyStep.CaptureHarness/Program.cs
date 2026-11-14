using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MistyStep.Core.Capture;
using MistyStep.Core.Config;
using MistyStep.Core.Models;
using MistyStep.Core.Store;
using MistyStep.Core.Time;
using MistyStep.Windows.Clipboard;
using MistyStep.Windows.Imaging;

namespace MistyStep.CaptureHarness;

/// <summary>
/// M3 console harness. Watches the clipboard with the real watcher, decider and encoder, and prints
/// one line per copy with its type and size (never its content), plus the clipboard format names.
///   --quiet-formats   do not print format names
///   --debug           verbose watcher logging
/// While running: type r and press Enter to simulate the hotkey ("Share current clipboard now").
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var showFormats = !args.Contains("--quiet-formats");
        var debug = args.Contains("--debug");

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(debug ? LogLevel.Debug : LogLevel.Information));

        // The watcher awaits retries on the UI thread, which needs the WinForms context before the loop starts.
        var uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);

        var limits = new CaptureLimits { ExcludedApps = new AppConfig().ExcludedApps };
        var store = new ClipStore(SystemClock.Instance, 0);
        using var watcher = new ClipboardWatcher(loggerFactory.CreateLogger<ClipboardWatcher>());

        // Subscribed before the coordinator so format names print above the outcome line.
        if (showFormats)
            watcher.Captured += (clip, trigger) => Console.WriteLine($"{Now()} {Name(trigger),-8} formats: {string.Join(", ", clip.Formats.Order(StringComparer.Ordinal))}");

        using var coordinator = new CaptureCoordinator(watcher, store, new GdiImageEncoder(),
            () => new CaptureContext(Paused: false, CaptureMode.Automatic, HotkeyTriggered: false), () => limits, SystemClock.Instance);
        coordinator.Completed += outcome => Console.WriteLine($"{Now()} {Name(outcome.Trigger),-8} => {Describe(outcome)}");

        watcher.Start();
        Console.WriteLine($"Misty Step capture harness. Listening: {watcher.IsListening}.");
        Console.WriteLine("Copy text, images and files in any app. Type r + Enter to simulate the hotkey. Ctrl+C to quit.");
        Console.WriteLine();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            uiContext.Post(_ => Application.ExitThread(), null);
        };

        var input = new Thread(() =>
        {
            while (Console.ReadLine() is { } line)
            {
                if (line.Trim().Equals("r", StringComparison.OrdinalIgnoreCase)) uiContext.Post(_ => watcher.RequestRead(), null);
            }
        }) { IsBackground = true };
        input.Start();

        Application.Run();
        return 0;
    }

    private static string Now() => DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Name(ReadTrigger trigger) => trigger switch
    {
        ReadTrigger.ClipboardChange => "change",
        ReadTrigger.Watchdog => "watchdog",
        ReadTrigger.Hotkey => "hotkey",
        _ => trigger.ToString(),
    };

    private static string Describe(CaptureOutcome outcome) => outcome switch
    {
        CaptureOutcome.Stored { Type: ClipType.Text } s => $"TEXT, {s.Size:N0} bytes UTF-8",
        CaptureOutcome.Stored s => $"IMAGE from {s.Source}, {s.Mime}, {s.Width}x{s.Height}, {s.Size:N0} bytes",
        CaptureOutcome.Ignored i => $"IGNORED ({i.Reason}){(i.ShowHint ? ", tray hint" : "")}",
        CaptureOutcome.Superseded => "SUPERSEDED by a newer copy",
        CaptureOutcome.Failed f => $"FAILED ({f.ErrorType})",
        _ => outcome.GetType().Name,
    };
}
