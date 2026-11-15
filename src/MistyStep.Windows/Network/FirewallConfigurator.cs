using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using MistyStep.Core.Network;

namespace MistyStep.Windows.Network;

/// <summary>
/// Writes the two firewall rules from the spec with netsh. Runs only inside the elevated helper
/// (<c>MistyStep.exe --update-firewall &lt;port&gt;</c>), which the app starts with the runas verb.
/// </summary>
public static class FirewallConfigurator
{
    /// <summary>Exit code: success.</summary>
    public const int Ok = 0;

    /// <summary>Exit code: a rule could not be written (usually: not elevated).</summary>
    public const int Failed = 2;

    /// <summary>The app-side result of asking for the elevated helper.</summary>
    public enum HelperResult { Done, Cancelled, Failed }

    /// <summary>
    /// Removes every inbound rule for this program (including the broad ones Windows creates from its own
    /// prompt, and block rules from cancelling that prompt), then adds the two scoped rules.
    /// </summary>
    public static int Apply(string exePath, int port)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (exePath.Contains('"', StringComparison.Ordinal)) return Failed;

        var portText = port.ToString(CultureInfo.InvariantCulture);

        // "No rules match" is a non-zero exit too, so deletes are best effort.
        Netsh($"advfirewall firewall delete rule name=all dir=in program=\"{exePath}\"");
        Netsh($"advfirewall firewall delete rule name=\"{FirewallAssessment.TailscaleRuleName}\"");
        Netsh($"advfirewall firewall delete rule name=\"{FirewallAssessment.HomeWifiRuleName}\"");

        var tailscale = Netsh($"advfirewall firewall add rule name=\"{FirewallAssessment.TailscaleRuleName}\" dir=in action=allow " +
                              $"program=\"{exePath}\" protocol=TCP localport={portText} remoteip=100.64.0.0/10 profile=any");
        var wifi = Netsh($"advfirewall firewall add rule name=\"{FirewallAssessment.HomeWifiRuleName}\" dir=in action=allow " +
                         $"program=\"{exePath}\" protocol=TCP localport={portText} remoteip=LocalSubnet profile=private");
        return tailscale == 0 && wifi == 0 ? Ok : Failed;
    }

    /// <summary>Starts this program elevated as the firewall helper and waits for it. One UAC prompt.</summary>
    public static async Task<HelperResult> RunElevatedHelperAsync(string exePath, int port, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Create(CultureInfo.InvariantCulture, $"--update-firewall {port}"),
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null) return HelperResult.Failed;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == Ok ? HelperResult.Done : HelperResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return HelperResult.Cancelled; // ERROR_CANCELLED: the user said No to UAC.
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return HelperResult.Failed;
        }
    }

    private static int Netsh(string arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(30_000) ? process.ExitCode : -1;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
