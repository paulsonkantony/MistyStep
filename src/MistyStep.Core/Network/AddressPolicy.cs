using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace MistyStep.Core.Network;

/// <summary>Which remote addresses may connect at all. Anything not allowed is aborted without a response.</summary>
public static class AddressPolicy
{
    /// <summary>Tailscale's CGNAT range, 100.64.0.0/10.</summary>
    public static readonly IPNetwork TailscaleRange = new(IPAddress.Parse("100.64.0.0"), 10);

    /// <summary>Smallest LAN prefix honoured. A wider "subnet" would expose far more than a home network.</summary>
    public const int MinLanPrefixLength = 8;

    public static bool IsAllowed(IPAddress remote, NetworkSnapshot snapshot, bool tailscaleEnabled, bool lanEnabled)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;

        if (tailscaleEnabled && TailscaleRange.Contains(remote)) return true;

        if (lanEnabled)
        {
            foreach (var nic in snapshot.Interfaces)
            {
                if (nic.Kind != InterfaceKind.Physical || nic.Category != NetworkCategory.Private) continue;
                if (nic.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (nic.PrefixLength is < MinLanPrefixLength or > 32) continue;
                if (SubnetOf(nic.Address, nic.PrefixLength).Contains(remote)) return true;
            }
        }

        return false;
    }

    /// <summary>The network containing <paramref name="address"/> with the host bits cleared.</summary>
    internal static IPNetwork SubnetOf(IPAddress address, int prefixLength)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value & mask);
        return new IPNetwork(new IPAddress(bytes), prefixLength);
    }
}
