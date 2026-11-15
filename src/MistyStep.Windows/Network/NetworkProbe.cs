using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MistyStep.Core.Network;

namespace MistyStep.Windows.Network;

/// <summary>
/// Lists local IPv4 addresses and finds the Tailscale address (an interface that is up with an
/// address in 100.64.0.0/10). M5 adds the MagicDNS name and the Windows network category; until
/// then every LAN interface is reported as Public, so Home Wi-Fi mode allows nobody.
/// </summary>
public sealed class NetworkProbe : INetworkProbe
{
    private static readonly string[] VirtualMarkers =
        ["Hyper-V", "vEthernet", "VMware", "VirtualBox", "WSL", "Docker", "Virtual", "Loopback", "TAP-", "Npcap"];

    public Task<NetworkSnapshot> ProbeAsync(CancellationToken cancellationToken) => Task.Run(Probe, cancellationToken);

    public static NetworkSnapshot Probe()
    {
        IPAddress? tailscale = null;
        var interfaces = new List<LocalInterface>();

        foreach (var nic in SafeGetInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties properties;
            try { properties = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;

                InterfaceKind kind;
                if (AddressPolicy.TailscaleRange.Contains(address))
                {
                    kind = InterfaceKind.Tailscale;
                    tailscale ??= address;
                }
                else
                {
                    kind = IsVirtual(nic) ? InterfaceKind.Virtual : InterfaceKind.Physical;
                }

                interfaces.Add(new LocalInterface(address, unicast.PrefixLength, NetworkCategory.Public, kind));
            }
        }

        return new NetworkSnapshot(tailscale, MagicDnsName: null, Environment.MachineName, interfaces);
    }

    private static bool IsVirtual(NetworkInterface nic) =>
        VirtualMarkers.Any(m => nic.Description.Contains(m, StringComparison.OrdinalIgnoreCase)
                             || nic.Name.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static NetworkInterface[] SafeGetInterfaces()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return []; }
    }
}
