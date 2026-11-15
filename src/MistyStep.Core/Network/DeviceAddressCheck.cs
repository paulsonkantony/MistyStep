using System.Net;
using MistyStep.Core.Auth;

namespace MistyStep.Core.Network;

/// <summary>
/// Spots paired devices whose stored base URL no longer reaches this PC: the PC was renamed, its
/// Tailscale name or address changed, the port changed, or its LAN address moved within the same network.
/// Something only temporarily unavailable (Tailscale off, another Wi-Fi) is not stale.
/// </summary>
public static class DeviceAddressCheck
{
    public static IReadOnlyList<Device> StaleDevices(IEnumerable<Device> devices, NetworkSnapshot snapshot, int port)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(snapshot);
        return devices.Where(d => IsStale(d.BaseUrl, snapshot, port)).ToList();
    }

    internal static bool IsStale(string baseUrl, NetworkSnapshot snapshot, int port)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return true;
        if (uri.Port != port) return true;

        var host = PairingAddress.TrimDot(uri.Host) ?? "";
        if (IPAddress.TryParse(host, out var ip))
        {
            if (AddressPolicy.TailscaleRange.Contains(ip))
                return snapshot.TailscaleAddress is not null && !snapshot.TailscaleAddress.Equals(ip);

            // Stale only if this PC is on that same network now, under a different address.
            var sameNetwork = PairingAddress.PrivateLanInterfaces(snapshot)
                .Where(i => AddressPolicy.SubnetOf(i.Address, i.PrefixLength).Contains(ip))
                .ToList();
            return sameNetwork.Count > 0 && !sameNetwork.Any(i => i.Address.Equals(ip));
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return !string.Equals(host, snapshot.Hostname + ".local", StringComparison.OrdinalIgnoreCase);

        // Any other name is a MagicDNS name. Compare only when the current one is known.
        var magicDns = PairingAddress.TrimDot(snapshot.MagicDnsName);
        return magicDns is not null && !string.Equals(host, magicDns, StringComparison.OrdinalIgnoreCase);
    }
}
