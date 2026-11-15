using System.Net;

namespace MistyStep.Core.Network;

/// <summary>Windows network category, as reported by INetworkListManager.</summary>
public enum NetworkCategory { Public, Private, DomainAuthenticated }

public enum InterfaceKind { Physical, Tailscale, Virtual }

/// <summary>One IPv4 address on a local interface.</summary>
public sealed record LocalInterface(IPAddress Address, int PrefixLength, NetworkCategory Category, InterfaceKind Kind);

/// <summary>What the network probe found. Re-probed on network changes and resume.</summary>
public sealed record NetworkSnapshot(
    IPAddress? TailscaleAddress,
    string? MagicDnsName,
    string Hostname,
    IReadOnlyList<LocalInterface> Interfaces)
{
    public static NetworkSnapshot Empty(string hostname) => new(null, null, hostname, []);
}
