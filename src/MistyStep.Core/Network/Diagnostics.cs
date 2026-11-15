using System.Globalization;
using System.Text;
using MistyStep.Core.Auth;
using MistyStep.Core.Models;

namespace MistyStep.Core.Network;

public sealed record DiagnosticsInputs(
    string AppVersion,
    string OsVersion,
    bool ListenerRegistered,
    string? ServerError,
    int Port,
    bool TailscaleEnabled,
    bool LanEnabled,
    NetworkSnapshot Network,
    FirewallState Firewall,
    CaptureMode Mode,
    string PauseLine,
    int ExpiryMinutes,
    IReadOnlyList<Device> Devices,
    DateTimeOffset NowUtc);

/// <summary>
/// The diagnostic details shown in the Help and troubleshooting window, for the user's own troubleshooting.
/// Nothing is sent anywhere (no reporting of any kind). It holds no secrets anyway: no tokens, no token hashes,
/// no pairing or setup codes, and no clipboard content (only the inputs below are available to it).
/// </summary>
public static class Diagnostics
{
    public static string Build(DiagnosticsInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var health = Health.Evaluate(new HealthInputs(inputs.ListenerRegistered, inputs.ServerError, inputs.TailscaleEnabled, inputs.LanEnabled, inputs.Network, inputs.Firewall));
        var text = new StringBuilder();
        void Line(string line) => text.Append(line).Append('\n');

        Line($"Misty Step {inputs.AppVersion} diagnostics, {inputs.NowUtc.ToString("u", CultureInfo.InvariantCulture)}");
        Line($"Windows: {inputs.OsVersion}");
        Line("");
        Line("Health checks");
        foreach (var check in Enum.GetValues<HealthCheck>())
        {
            var failing = health.Failing == check;
            var passedEarlier = health.Failing is null || check < health.Failing;
            var mark = failing ? "FAIL" : passedEarlier ? "ok" : "not checked";
            Line($"  {Health.CheckName(check)}: {mark}{(failing ? $" ({health.Problem})" : "")}");
        }

        Line("");
        Line("Connection");
        Line(string.Create(CultureInfo.InvariantCulture, $"  Port: {inputs.Port}"));
        Line($"  Tailscale mode: {OnOff(inputs.TailscaleEnabled)}; address: {inputs.Network.TailscaleAddress?.ToString() ?? "none"}; name: {inputs.Network.MagicDnsName ?? "none"}");
        Line($"  Home Wi-Fi mode: {OnOff(inputs.LanEnabled)}; host name: {inputs.Network.Hostname}");
        foreach (var nic in inputs.Network.Interfaces)
            Line(string.Create(CultureInfo.InvariantCulture, $"  Interface: {nic.Address}/{nic.PrefixLength}, {nic.Kind}, {nic.Category}"));
        Line($"  Firewall: {inputs.Firewall}");
        Line("");
        Line("Sharing");
        Line($"  Mode: {inputs.Mode}; {inputs.PauseLine}");
        Line(inputs.ExpiryMinutes == 0
            ? "  Shared item lasts until replaced"
            : string.Create(CultureInfo.InvariantCulture, $"  Shared item expires after {inputs.ExpiryMinutes} min"));
        Line("");
        Line(string.Create(CultureInfo.InvariantCulture, $"Devices ({inputs.Devices.Count})"));
        foreach (var device in inputs.Devices)
        {
            var seen = device.LastSeenUtc is { } s ? s.ToString("u", CultureInfo.InvariantCulture) : "never";
            Line(string.Create(CultureInfo.InvariantCulture, $"  {device.Name}: last seen {seen}, {device.FetchCount} fetched, {device.BaseUrl}"));
        }

        return text.ToString();
    }

    private static string OnOff(bool value) => value ? "on" : "off";
}
