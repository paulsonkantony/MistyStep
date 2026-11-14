using System.Globalization;
using System.Net.Sockets;

namespace MistyStep.Core.Network;

/// <summary>
/// Checks the Host header against the names this PC is known by, each with the configured port.
/// Blocks DNS-rebinding requests from a browser on the PC.
/// </summary>
public static class HostValidator
{
    public static bool IsAllowed(string? hostHeader, NetworkSnapshot snapshot, int port)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(hostHeader) || hostHeader.StartsWith('[')) return false;

        var colon = hostHeader.LastIndexOf(':');
        if (colon <= 0 || colon == hostHeader.Length - 1) return false;

        var portText = hostHeader[(colon + 1)..];
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var headerPort) || headerPort != port)
            return false;

        var name = TrimDot(hostHeader[..colon]);
        if (name.Length == 0) return false;

        foreach (var allowed in AllowedNames(snapshot))
        {
            if (string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static IEnumerable<string> AllowedNames(NetworkSnapshot snapshot)
    {
        yield return "localhost";
        yield return "127.0.0.1";

        if (snapshot.TailscaleAddress is not null) yield return snapshot.TailscaleAddress.ToString();
        if (!string.IsNullOrWhiteSpace(snapshot.MagicDnsName)) yield return TrimDot(snapshot.MagicDnsName);
        if (!string.IsNullOrWhiteSpace(snapshot.Hostname)) yield return snapshot.Hostname + ".local";

        foreach (var nic in snapshot.Interfaces)
        {
            if (nic.Address.AddressFamily == AddressFamily.InterNetwork) yield return nic.Address.ToString();
        }
    }

    private static string TrimDot(string name) => name.EndsWith('.') ? name[..^1] : name;
}
