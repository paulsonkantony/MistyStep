using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MistyStep.Core.Network;

/// <summary>How a device reaches the PC. Both can be on at once; each device keeps the base URL it was given.</summary>
public enum ConnectionMode { Tailscale, HomeWifi }

/// <summary>
/// Picks the host for a new pairing's base URL (spec, Networking modes):
/// Tailscale uses the MagicDNS name, else the 100.x address; Home Wi-Fi uses <c>&lt;hostname&gt;.local</c>,
/// else the LAN IP. <c>useIp</c> is the wizard's "Try the IP address instead".
/// </summary>
public static class PairingAddress
{
    /// <summary>The host to put in the setup code, or null if the mode has no address right now.</summary>
    public static string? ChooseHost(ConnectionMode mode, NetworkSnapshot snapshot, bool useIp)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        switch (mode)
        {
            case ConnectionMode.Tailscale:
                if (snapshot.TailscaleAddress is null) return null;
                if (!useIp && TrimDot(snapshot.MagicDnsName) is { Length: > 0 } magicDns) return magicDns;
                return snapshot.TailscaleAddress.ToString();

            case ConnectionMode.HomeWifi:
                var lan = LanAddress(snapshot);
                if (lan is null) return null;
                if (!useIp && !string.IsNullOrWhiteSpace(snapshot.Hostname)) return snapshot.Hostname + ".local";
                return lan.ToString();

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>True when the mode has an address the iPad could use.</summary>
    public static bool HasAddress(ConnectionMode mode, NetworkSnapshot snapshot) => ChooseHost(mode, snapshot, useIp: true) is not null;

    /// <summary>True when the host is a plain IP address (no name to resolve).</summary>
    public static bool IsIpHost(string host) => IPAddress.TryParse(host, out _);

    public static string BaseUrl(string host, int port) => string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}");

    /// <summary>The first IPv4 address on a physical interface whose Windows network category is Private.</summary>
    public static IPAddress? LanAddress(NetworkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return PrivateLanInterfaces(snapshot).Select(i => i.Address).FirstOrDefault();
    }

    /// <summary>Interfaces Home Wi-Fi mode may serve: physical, IPv4, Private, with a plausible prefix.</summary>
    public static IEnumerable<LocalInterface> PrivateLanInterfaces(NetworkSnapshot snapshot) =>
        snapshot.Interfaces.Where(i =>
            i.Kind == InterfaceKind.Physical
            && i.Category == NetworkCategory.Private
            && i.Address.AddressFamily == AddressFamily.InterNetwork
            && i.PrefixLength is >= AddressPolicy.MinLanPrefixLength and <= 32);

    /// <summary>True when there is a physical IPv4 network, but Windows marks none of them Private.</summary>
    public static bool OnlyPublicLan(NetworkSnapshot snapshot) =>
        !PrivateLanInterfaces(snapshot).Any()
        && snapshot.Interfaces.Any(i => i.Kind == InterfaceKind.Physical && i.Address.AddressFamily == AddressFamily.InterNetwork);

    internal static string? TrimDot(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.EndsWith('.') ? name[..^1] : name;
}
