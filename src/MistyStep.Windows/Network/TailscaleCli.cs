using System.ComponentModel;
using System.Diagnostics;
using MistyStep.Core.Network;

namespace MistyStep.Windows.Network;

/// <summary>
/// Gets the MagicDNS name by running <c>tailscale status --json</c> with a 3 s timeout (spec, Detecting Tailscale).
/// The output is parsed in memory and never logged. Any failure gives null, and the 100.x address is used.
/// </summary>
public static class TailscaleCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static async Task<string?> GetMagicDnsNameAsync(CancellationToken cancellationToken)
    {
        var exe = FindExecutable();
        if (exe is null) return null;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                ArgumentList = { "status", "--json" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        try
        {
            if (!process.Start()) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? TailscaleStatus.ReadDnsName(await output.ConfigureAwait(false)) : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or Win32Exception or InvalidOperationException or IOException)
        {
            TryKill(process);
            return null;
        }
    }

    /// <summary><c>tailscale.exe</c> on the PATH, else in <c>%ProgramFiles%\Tailscale</c>.</summary>
    internal static string? FindExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "tailscale.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "Tailscale", "tailscale.exe");
        return File.Exists(installed) ? installed : null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
