using System.Diagnostics;
using MistyStep.Core.Network;
using MistyStep.Windows.Network;

namespace MistyStep.Windows.Tests;

/// <summary>
/// Smoke tests against the real PC: they check the COM and process calls work and stay quick,
/// not what this particular machine's network looks like.
/// </summary>
public class NetworkSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void Probe_reads_interfaces_and_categories()
    {
        var watch = Stopwatch.StartNew();
        var snapshot = NetworkProbe.Probe(magicDnsName: "pc.tailnet.ts.net");
        output.WriteLine($"Probe took {watch.ElapsedMilliseconds} ms");

        Assert.False(string.IsNullOrWhiteSpace(snapshot.Hostname));
        Assert.Equal(snapshot.TailscaleAddress is null ? null : "pc.tailnet.ts.net", snapshot.MagicDnsName);
        foreach (var nic in snapshot.Interfaces)
            output.WriteLine($"{nic.Kind} /{nic.PrefixLength} {nic.Category}");
        Assert.True(watch.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void Firewall_rules_can_be_read_without_elevation()
    {
        var watch = Stopwatch.StartNew();
        var read = FirewallReader.Read();
        output.WriteLine($"Firewall read took {watch.ElapsedMilliseconds} ms");

        Assert.NotNull(read);
        output.WriteLine($"{read.Value.Rules.Count} rules, enabled profiles: {read.Value.EnabledProfiles}");
        Assert.NotEmpty(read.Value.Rules);
    }

    [Fact]
    public async Task Tailscale_cli_returns_a_name_or_null_quickly()
    {
        var watch = Stopwatch.StartNew();
        var name = await TailscaleCli.GetMagicDnsNameAsync(TestContext.Current.CancellationToken);
        output.WriteLine($"tailscale status took {watch.ElapsedMilliseconds} ms; name {(name is null ? "not found" : "found")}");
        Assert.True(watch.ElapsedMilliseconds < 4500);
        if (name is not null) Assert.EndsWith(".ts.net", name, StringComparison.Ordinal);
    }
}
