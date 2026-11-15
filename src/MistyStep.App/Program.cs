using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace MistyStep.App;

internal static class Program
{
    private const string MutexName = @"Local\MistyStep.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);

        // One instance per Windows session. (M6 makes the second instance open the first one's menu.)
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Misty Step is already running. Look for its icon in the notification area.", "Misty Step",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Started before any control exists, so the server start-up captures no UI synchronization context.
        var host = AppHost.Start(options);

        Application.ThreadException += (_, e) => host.Log.LogError(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) host.Log.LogCritical(ex, "Unhandled exception");
        };

        try
        {
            using var tray = new TrayApp(host, options);
            Application.Run(tray);
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return 0;
    }
}
