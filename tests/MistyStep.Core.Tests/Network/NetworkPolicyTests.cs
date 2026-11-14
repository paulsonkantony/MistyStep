using System.Net;
using MistyStep.Core.Network;

namespace MistyStep.Core.Tests.Network;

public class AddressPolicyTests
{
    private static readonly NetworkSnapshot Snapshot = new(
        TailscaleAddress: IPAddress.Parse("100.101.102.103"),
        MagicDnsName: "my-laptop.tailnet-name.ts.net.",
        Hostname: "MY-LAPTOP",
        Interfaces:
        [
            new LocalInterface(IPAddress.Parse("192.168.1.20"), 24, NetworkCategory.Private, InterfaceKind.Physical),
            new LocalInterface(IPAddress.Parse("10.0.0.5"), 16, NetworkCategory.Public, InterfaceKind.Physical),
            new LocalInterface(IPAddress.Parse("172.20.0.1"), 20, NetworkCategory.Private, InterfaceKind.Virtual),
            new LocalInterface(IPAddress.Parse("100.101.102.103"), 32, NetworkCategory.Public, InterfaceKind.Tailscale),
            new LocalInterface(IPAddress.Parse("44.0.0.1"), 2, NetworkCategory.Private, InterfaceKind.Physical),
        ]);

    [Theory]
    // Loopback: always allowed, whatever the modes.
    [InlineData("127.0.0.1", false, false, true)]
    [InlineData("127.5.6.7", false, false, true)]
    [InlineData("::1", false, false, true)]
    // Tailscale range 100.64.0.0/10, edges included.
    [InlineData("100.64.0.1", true, false, true)]
    [InlineData("100.127.255.254", true, false, true)]
    [InlineData("100.128.0.1", true, false, false)]
    [InlineData("100.63.255.255", true, false, false)]
    [InlineData("100.64.0.1", false, true, false)]
    // LAN: inside and outside the Private physical subnet.
    [InlineData("192.168.1.77", false, true, true)]
    [InlineData("192.168.1.77", true, false, false)]
    [InlineData("192.168.2.77", false, true, false)]
    // A Public network's subnet is never allowed.
    [InlineData("10.0.4.4", false, true, false)]
    // Virtual adapters are skipped.
    [InlineData("172.20.0.9", false, true, false)]
    // An implausibly wide "subnet" (/2) is not honoured.
    [InlineData("8.8.8.8", false, true, false)]
    // A public address.
    [InlineData("8.8.8.8", true, true, false)]
    [InlineData("203.0.113.9", true, true, false)]
    // IPv4-mapped IPv6 is treated as IPv4; other IPv6 is refused.
    [InlineData("::ffff:192.168.1.77", false, true, true)]
    [InlineData("::ffff:100.64.0.1", true, false, true)]
    [InlineData("fe80::1", true, true, false)]
    public void IsAllowed(string remote, bool tailscale, bool lan, bool expected) =>
        Assert.Equal(expected, AddressPolicy.IsAllowed(IPAddress.Parse(remote), Snapshot, tailscale, lan));

    [Fact]
    public void No_interfaces_allows_only_loopback_and_tailscale()
    {
        var empty = NetworkSnapshot.Empty("PC");
        Assert.True(AddressPolicy.IsAllowed(IPAddress.Loopback, empty, false, true));
        Assert.True(AddressPolicy.IsAllowed(IPAddress.Parse("100.100.100.100"), empty, true, true));
        Assert.False(AddressPolicy.IsAllowed(IPAddress.Parse("192.168.1.2"), empty, true, true));
    }

    [Fact]
    public void SubnetOf_clears_host_bits()
    {
        var network = AddressPolicy.SubnetOf(IPAddress.Parse("192.168.1.20"), 24);
        Assert.Equal(IPAddress.Parse("192.168.1.0"), network.BaseAddress);
        Assert.Equal(24, network.PrefixLength);
    }
}

public class HostValidatorTests
{
    private const int Port = 45654;

    private static readonly NetworkSnapshot Snapshot = new(
        TailscaleAddress: IPAddress.Parse("100.101.102.103"),
        MagicDnsName: "my-laptop.tailnet-name.ts.net.",
        Hostname: "MY-LAPTOP",
        Interfaces: [new LocalInterface(IPAddress.Parse("192.168.1.20"), 24, NetworkCategory.Private, InterfaceKind.Physical)]);

    [Theory]
    [InlineData("100.101.102.103:45654")]
    [InlineData("my-laptop.tailnet-name.ts.net:45654")]
    [InlineData("my-laptop.tailnet-name.ts.net.:45654")]
    [InlineData("MY-LAPTOP.TAILNET-NAME.TS.NET:45654")]
    [InlineData("my-laptop.local:45654")]
    [InlineData("192.168.1.20:45654")]
    [InlineData("localhost:45654")]
    [InlineData("127.0.0.1:45654")]
    public void Known_hosts_with_port_are_allowed(string host) =>
        Assert.True(HostValidator.IsAllowed(host, Snapshot, Port));

    [Theory]
    [InlineData("evil.example.com:45654")]
    [InlineData("100.101.102.104:45654")]
    [InlineData("my-laptop:45654")]
    [InlineData("localhost")]
    [InlineData("localhost:80")]
    [InlineData("localhost:")]
    [InlineData(":45654")]
    [InlineData("localhost:45654:45654")]
    [InlineData("localhost:+45654")]
    [InlineData("[::1]:45654")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Foreign_or_malformed_hosts_are_refused(string? host) =>
        Assert.False(HostValidator.IsAllowed(host, Snapshot, Port));

    [Fact]
    public void Missing_tailscale_does_not_allow_tailscale_names()
    {
        var lanOnly = Snapshot with { TailscaleAddress = null, MagicDnsName = null };
        Assert.False(HostValidator.IsAllowed("100.101.102.103:45654", lanOnly, Port));
        Assert.True(HostValidator.IsAllowed("192.168.1.20:45654", lanOnly, Port));
    }
}
