using System.Net;
using MistyStep.Core.Auth;
using MistyStep.Core.Network;

namespace MistyStep.Core.Tests.Network;

internal static class Snapshots
{
    public static readonly IPAddress Tailscale = IPAddress.Parse("100.101.102.103");

    public static LocalInterface Lan(string ip, NetworkCategory category = NetworkCategory.Private, int prefix = 24, InterfaceKind kind = InterfaceKind.Physical) =>
        new(IPAddress.Parse(ip), prefix, category, kind);

    public static NetworkSnapshot Make(IPAddress? tailscale = null, string? magicDns = null, string hostname = "MY-LAPTOP", params LocalInterface[] interfaces) =>
        new(tailscale, magicDns, hostname, interfaces);

    public static NetworkSnapshot Full => Make(Tailscale, "my-laptop.tailnet-name.ts.net.", "MY-LAPTOP",
        Lan("192.168.1.20"), new LocalInterface(Tailscale, 32, NetworkCategory.Private, InterfaceKind.Tailscale));
}

public class PairingAddressTests
{
    [Fact]
    public void Tailscale_prefers_the_magicdns_name_without_its_trailing_dot() =>
        Assert.Equal("my-laptop.tailnet-name.ts.net", PairingAddress.ChooseHost(ConnectionMode.Tailscale, Snapshots.Full, useIp: false));

    [Fact]
    public void Tailscale_falls_back_to_the_address_without_magicdns_or_when_asked()
    {
        var noName = Snapshots.Make(Snapshots.Tailscale, magicDns: null);
        Assert.Equal("100.101.102.103", PairingAddress.ChooseHost(ConnectionMode.Tailscale, noName, useIp: false));
        Assert.Equal("100.101.102.103", PairingAddress.ChooseHost(ConnectionMode.Tailscale, Snapshots.Full, useIp: true));
    }

    [Fact]
    public void Tailscale_has_no_host_when_tailscale_is_missing() =>
        Assert.Null(PairingAddress.ChooseHost(ConnectionMode.Tailscale, Snapshots.Make(interfaces: Snapshots.Lan("192.168.1.20")), useIp: false));

    [Fact]
    public void Home_wifi_uses_dot_local_then_the_lan_ip()
    {
        Assert.Equal("MY-LAPTOP.local", PairingAddress.ChooseHost(ConnectionMode.HomeWifi, Snapshots.Full, useIp: false));
        Assert.Equal("192.168.1.20", PairingAddress.ChooseHost(ConnectionMode.HomeWifi, Snapshots.Full, useIp: true));
    }

    [Fact]
    public void Home_wifi_needs_a_private_physical_interface()
    {
        var publicOnly = Snapshots.Make(interfaces: Snapshots.Lan("192.168.1.20", NetworkCategory.Public));
        var virtualOnly = Snapshots.Make(interfaces: Snapshots.Lan("172.20.0.1", kind: InterfaceKind.Virtual));
        var tooWide = Snapshots.Make(interfaces: Snapshots.Lan("44.0.0.1", prefix: 2));

        Assert.Null(PairingAddress.ChooseHost(ConnectionMode.HomeWifi, publicOnly, useIp: false));
        Assert.Null(PairingAddress.ChooseHost(ConnectionMode.HomeWifi, virtualOnly, useIp: false));
        Assert.Null(PairingAddress.ChooseHost(ConnectionMode.HomeWifi, tooWide, useIp: false));
        Assert.True(PairingAddress.OnlyPublicLan(publicOnly));
        Assert.False(PairingAddress.OnlyPublicLan(Snapshots.Full));
        Assert.False(PairingAddress.OnlyPublicLan(Snapshots.Make()));
    }

    [Fact]
    public void Base_url_and_ip_detection()
    {
        Assert.Equal("http://MY-LAPTOP.local:45654", PairingAddress.BaseUrl("MY-LAPTOP.local", 45654));
        Assert.True(PairingAddress.IsIpHost("192.168.1.20"));
        Assert.False(PairingAddress.IsIpHost("MY-LAPTOP.local"));
    }

    [Fact]
    public void Chosen_hosts_pass_the_host_check()
    {
        foreach (var mode in new[] { ConnectionMode.Tailscale, ConnectionMode.HomeWifi })
        foreach (var useIp in new[] { false, true })
        {
            var host = PairingAddress.ChooseHost(mode, Snapshots.Full, useIp)!;
            Assert.True(HostValidator.IsAllowed($"{host}:45654", Snapshots.Full, 45654), $"{mode} {useIp}: {host}");
        }
    }
}

public class TailscaleStatusTests
{
    [Fact]
    public void Reads_self_dns_name_and_trims_the_dot() =>
        Assert.Equal("pc.tail1234.ts.net", TailscaleStatus.ReadDnsName("""{"Version":"1.76","Self":{"DNSName":"pc.tail1234.ts.net.","HostName":"PC"}}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"Self":null}""")]
    [InlineData("""{"Self":{"DNSName":""}}""")]
    [InlineData("""{"Self":{"DNSName":42}}""")]
    [InlineData("""{"Self":{"DNSName":"evil.com/x?"}}""")]
    [InlineData("""{"Self":{"DNSName":"single"}}""")]
    [InlineData("""{"Self":{"DNSName":"a..b"}}""")]
    [InlineData("""{"Self":{"DNSName":"-a.b"}}""")]
    public void Anything_else_gives_null(string? json) => Assert.Null(TailscaleStatus.ReadDnsName(json));
}

public class FirewallAssessmentTests
{
    private const string Exe = @"C:\Program Files\Misty Step\MistyStep.exe";
    private const int Port = 45654;

    private static FirewallRule Rule(string name, bool allow = true, string? program = Exe, string? ports = "45654",
        FirewallProfiles profiles = FirewallProfiles.All, bool enabled = true, bool inbound = true, int protocol = 6) =>
        new(name, enabled, inbound, allow, program, ports, protocol, profiles);

    private static readonly FirewallRule TailscaleRule = Rule(FirewallAssessment.TailscaleRuleName);
    private static readonly FirewallRule WifiRule = Rule(FirewallAssessment.HomeWifiRuleName, profiles: FirewallProfiles.Private);

    private static FirewallState Assess(bool tailscale, bool lan, FirewallProfiles enabled = FirewallProfiles.All, params FirewallRule[] rules) =>
        FirewallAssessment.Assess(rules, enabled, Exe, Port, tailscale, lan);

    [Fact]
    public void Both_rules_present_is_ok() => Assert.Equal(FirewallState.Ok, Assess(true, true, rules: [TailscaleRule, WifiRule]));

    [Fact]
    public void Only_the_rules_for_enabled_modes_are_needed()
    {
        Assert.Equal(FirewallState.Ok, Assess(true, false, rules: [TailscaleRule]));
        Assert.Equal(FirewallState.Ok, Assess(false, true, rules: [WifiRule]));
        Assert.Equal(FirewallState.Ok, Assess(false, false));
        Assert.Equal(FirewallState.MissingRule, Assess(true, false, rules: [WifiRule]));
        Assert.Equal(FirewallState.MissingRule, Assess(false, true, rules: [TailscaleRule]));
    }

    [Fact]
    public void A_rule_that_does_not_match_counts_as_missing()
    {
        Assert.Equal(FirewallState.MissingRule, Assess(true, false, rules: [Rule(FirewallAssessment.TailscaleRuleName, enabled: false)]));
        Assert.Equal(FirewallState.MissingRule, Assess(true, false, rules: [Rule(FirewallAssessment.TailscaleRuleName, ports: "8080")]));
        Assert.Equal(FirewallState.MissingRule, Assess(true, false, rules: [Rule(FirewallAssessment.TailscaleRuleName, program: @"C:\Old\MistyStep.exe")]));
        Assert.Equal(FirewallState.MissingRule, Assess(true, false, rules: [Rule(FirewallAssessment.TailscaleRuleName, protocol: 17)]));
        Assert.Equal(FirewallState.MissingRule, Assess(true, false, rules: [Rule(FirewallAssessment.TailscaleRuleName, inbound: false)]));
        Assert.Equal(FirewallState.MissingRule, Assess(false, true, rules: [Rule(FirewallAssessment.HomeWifiRuleName, profiles: FirewallProfiles.Public)]));
    }

    [Fact]
    public void Program_paths_compare_case_insensitively() =>
        Assert.Equal(FirewallState.Ok, Assess(true, false, rules: [Rule(FirewallAssessment.TailscaleRuleName, program: Exe.ToUpperInvariant())]));

    [Fact]
    public void A_block_rule_for_this_program_wins()
    {
        var block = Rule("mistystep.exe", allow: false, ports: null, profiles: FirewallProfiles.Public);
        Assert.Equal(FirewallState.Blocked, Assess(true, true, rules: [TailscaleRule, WifiRule, block]));
        // Disabled, outbound, or for another program: ignored.
        Assert.Equal(FirewallState.Ok, Assess(true, true, rules: [TailscaleRule, WifiRule, block with { Enabled = false }]));
        Assert.Equal(FirewallState.Ok, Assess(true, true, rules: [TailscaleRule, WifiRule, block with { Inbound = false }]));
        Assert.Equal(FirewallState.Ok, Assess(true, true, rules: [TailscaleRule, WifiRule, block with { ApplicationName = @"C:\x.exe" }]));
        // A block rule only for a profile whose firewall is off does nothing.
        Assert.Equal(FirewallState.Ok, Assess(true, true, FirewallProfiles.Private | FirewallProfiles.Domain, TailscaleRule, WifiRule, block));
    }

    [Fact]
    public void Firewall_off_needs_no_rules()
    {
        Assert.Equal(FirewallState.Ok, Assess(true, true, FirewallProfiles.None));
        // Private profile off: the Home Wi-Fi rule is not needed, the Tailscale one still is.
        Assert.Equal(FirewallState.Ok, Assess(false, true, FirewallProfiles.Public));
        Assert.Equal(FirewallState.MissingRule, Assess(true, true, FirewallProfiles.Public));
    }
}

public class DeviceAddressCheckTests
{
    private static Device DeviceAt(string baseUrl) => new("d1", "iPad", baseUrl, DateTime.UtcNow, null, 0);

    private static bool Stale(string baseUrl, NetworkSnapshot snapshot, int port = 45654) =>
        DeviceAddressCheck.StaleDevices([DeviceAt(baseUrl)], snapshot, port).Count == 1;

    [Theory]
    [InlineData("http://100.101.102.103:45654")]
    [InlineData("http://my-laptop.tailnet-name.ts.net:45654")]
    [InlineData("http://MY-LAPTOP.local:45654")]
    [InlineData("http://my-laptop.local:45654")]
    [InlineData("http://192.168.1.20:45654")]
    public void Current_addresses_are_not_stale(string baseUrl) => Assert.False(Stale(baseUrl, Snapshots.Full));

    [Theory]
    [InlineData("http://100.101.102.200:45654")] // Tailscale address changed
    [InlineData("http://old-name.tailnet-name.ts.net:45654")] // Tailscale name changed
    [InlineData("http://OLD-NAME.local:45654")] // PC renamed
    [InlineData("http://192.168.1.99:45654")] // DHCP gave the PC a new address on the same network
    [InlineData("http://100.101.102.103:8080")] // port changed
    [InlineData("https://100.101.102.103:45654")]
    [InlineData("not a url")]
    public void Changed_addresses_are_stale(string baseUrl) => Assert.True(Stale(baseUrl, Snapshots.Full));

    [Fact]
    public void Temporarily_unavailable_is_not_stale()
    {
        // Tailscale off: its address and name are unknown, so nothing to compare.
        var noTailscale = Snapshots.Make(interfaces: Snapshots.Lan("192.168.1.20"));
        Assert.False(Stale("http://100.101.102.103:45654", noTailscale));
        Assert.False(Stale("http://my-laptop.tailnet-name.ts.net:45654", noTailscale));

        // On another network (a café), the home LAN address is not "stale".
        var elsewhere = Snapshots.Make(Snapshots.Tailscale, interfaces: Snapshots.Lan("10.0.0.5", NetworkCategory.Public, 16));
        Assert.False(Stale("http://192.168.1.20:45654", elsewhere));

        // MagicDNS lookup failed this time: no verdict on the name.
        Assert.False(Stale("http://my-laptop.tailnet-name.ts.net:45654", Snapshots.Make(Snapshots.Tailscale)));
    }
}

public class HealthTests
{
    private static HealthInputs Inputs(bool listener = true, string? serverError = null, bool tailscale = true, bool lan = false,
        NetworkSnapshot? network = null, FirewallState firewall = FirewallState.Ok) =>
        new(listener, serverError, tailscale, lan, network ?? Snapshots.Full, firewall);

    [Fact]
    public void All_good_is_healthy() => Assert.True(Health.Evaluate(Inputs()).IsHealthy);

    [Fact]
    public void Checks_report_in_order()
    {
        var everythingWrong = Inputs(listener: false, serverError: "Port 45654 is already in use", network: Snapshots.Make(), firewall: FirewallState.Blocked);
        Assert.Equal(HealthCheck.ClipboardListener, Health.Evaluate(everythingWrong).Failing);
        Assert.Equal(HealthCheck.Server, Health.Evaluate(everythingWrong with { ListenerRegistered = true }).Failing);
        Assert.Equal("Port 45654 is already in use", Health.Evaluate(everythingWrong with { ListenerRegistered = true }).Problem);
        Assert.Equal(HealthCheck.Address, Health.Evaluate(everythingWrong with { ListenerRegistered = true, ServerError = null }).Failing);
        Assert.Equal(HealthCheck.Firewall, Health.Evaluate(Inputs(firewall: FirewallState.Blocked)).Failing);
        Assert.Equal(HealthCheck.Firewall, Health.Evaluate(Inputs(firewall: FirewallState.MissingRule)).Failing);
    }

    [Fact]
    public void One_working_mode_is_enough()
    {
        var lanOnly = Snapshots.Make(interfaces: Snapshots.Lan("192.168.1.20"));
        Assert.True(Health.Evaluate(Inputs(tailscale: true, lan: true, network: lanOnly)).IsHealthy);
        Assert.Equal(HealthCheck.Address, Health.Evaluate(Inputs(tailscale: true, lan: false, network: lanOnly)).Failing);
    }

    [Fact]
    public void Address_problem_names_what_is_missing()
    {
        var empty = Snapshots.Make();
        Assert.Contains("No connection set up", Health.Evaluate(Inputs(tailscale: false, lan: false, network: empty)).Problem);
        Assert.Contains("Tailscale is not connected", Health.Evaluate(Inputs(tailscale: true, lan: false, network: empty)).Problem);
        Assert.Contains("not on a Private network", Health.Evaluate(Inputs(tailscale: false, lan: true, network: empty)).Problem);
    }

    [Fact]
    public void Unknown_firewall_state_is_not_a_problem() =>
        Assert.True(Health.Evaluate(Inputs(firewall: FirewallState.Unknown)).IsHealthy);
}
