using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MistyStep.Core.Network;

namespace MistyStep.Windows.Network;

/// <summary>
/// Lists local IPv4 addresses with their Windows network category, and finds the Tailscale address
/// (an interface that is up with an address in 100.64.0.0/10). The MagicDNS name needs a process run,
/// so the caller passes it in and refreshes it only when the Tailscale address changes.
/// </summary>
public static class NetworkProbe
{
    private static readonly string[] VirtualMarkers =
        ["Hyper-V", "vEthernet", "VMware", "VirtualBox", "WSL", "Docker", "Virtual", "Loopback", "TAP-", "Npcap"];

    public static NetworkSnapshot Probe(string? magicDnsName)
    {
        IPAddress? tailscale = null;
        var interfaces = new List<LocalInterface>();
        var categoryOf = NetworkCategoryReader.Open();

        foreach (var nic in SafeGetInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties properties;
            try { properties = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            NetworkCategory? category = null;
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

                category ??= categoryOf(nic.Id);
                interfaces.Add(new LocalInterface(address, unicast.PrefixLength, category.Value, kind));
            }
        }

        return new NetworkSnapshot(tailscale, tailscale is null ? null : magicDnsName, HostName(), interfaces);
    }

    /// <summary>The DNS host name, which Windows answers to as <c>&lt;name&gt;.local</c> over mDNS.</summary>
    private static string HostName()
    {
        try
        {
            var name = Dns.GetHostName();
            return string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name;
        }
        catch (SocketException)
        {
            return Environment.MachineName;
        }
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
