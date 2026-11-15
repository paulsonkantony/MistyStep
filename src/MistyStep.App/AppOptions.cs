using System.Globalization;

namespace MistyStep.App;

/// <summary>
/// Command-line switches from the spec: --minimized, --portable, --log-level debug, and
/// --update-firewall &lt;port&gt; (the elevated helper that rewrites the two firewall rules, then exits).
/// </summary>
internal sealed record AppOptions(bool Minimized, bool Portable, bool DebugLogging, int? UpdateFirewallPort = null)
{
    public static AppOptions Parse(string[] args)
    {
        var minimized = false;
        var portable = false;
        var debug = false;
        int? firewallPort = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--minimized":
                    minimized = true;
                    break;
                case "--portable":
                    portable = true;
                    break;
                case "--log-level":
                    if (i + 1 < args.Length && args[i + 1].Equals("debug", StringComparison.OrdinalIgnoreCase)) debug = true;
                    i++;
                    break;
                case "--update-firewall":
                    if (i + 1 < args.Length
                        && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                        && port is >= 1 and <= 65535)
                    {
                        firewallPort = port;
                    }
                    else
                    {
                        firewallPort = 0; // present but invalid: the helper exits with an error
                    }

                    i++;
                    break;
            }
        }

        return new AppOptions(minimized, portable, debug, firewallPort);
    }
}
