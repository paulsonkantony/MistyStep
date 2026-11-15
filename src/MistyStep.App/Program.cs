using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using MistyStep.Windows.Network;

namespace MistyStep.App;

internal static class Program
{
    private const string MutexName = @"Local\MistyStep.SingleInstance";
    private const string ActivateEventName = @"Local\MistyStep.Activate";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);

        // Elevated firewall helper: rewrite the two rules and exit. No mutex, no UI, no settings.
        if (options.UpdateFirewallPort is { } firewallPort)
        {
            return firewallPort == 0 || Environment.ProcessPath is not { } exePath
                ? FirewallConfigurator.Failed
                : FirewallConfigurator.Apply(exePath, firewallPort);
        }

        // One instance per Windows session. A second launch signals the first to open its menu, then exits.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        if (!createdNew)
        {
            activate.Set();
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Started before any control exists, so the server start-up captures no UI synchronization context.
        var host = AppHost.Start(options);

        // Logged without content; the tray says something went wrong. Windows restarts the app after a crash.
        Application.ThreadException += (_, e) =>
        {
            host.Log.LogError(e.Exception, "Unhandled UI exception");
            host.NotifyProblem("Something went wrong, but Misty Step is still running. Details are in the log.");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) host.Log.LogCritical(ex, "Unhandled exception");
        };

        RegisteredWaitHandle? activation = null;
        try
        {
            using var tray = new TrayApp(host, options);
            activation = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => tray.OnActivationRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
            Application.Run(tray);
        }
        finally
        {
            activation?.Unregister(null);
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return 0;
    }
}
