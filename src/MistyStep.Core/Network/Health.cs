namespace MistyStep.Core.Network;

/// <summary>The four health checks, in the order they are reported (spec, Robustness: Health status).</summary>
public enum HealthCheck { ClipboardListener, Server, Address, Firewall }

public sealed record HealthInputs(
    bool ListenerRegistered,
    string? ServerError,
    bool TailscaleEnabled,
    bool LanEnabled,
    NetworkSnapshot Network,
    FirewallState Firewall);

/// <summary>The first failing check and a short line for the tray, or healthy.</summary>
public sealed record HealthReport(HealthCheck? Failing, string? Problem)
{
    public static readonly HealthReport Healthy = new(null, null);
    public bool IsHealthy => Failing is null;
}

public static class Health
{
    public static HealthReport Evaluate(HealthInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (!inputs.ListenerRegistered)
            return new(HealthCheck.ClipboardListener, "Clipboard listener is not running");

        if (inputs.ServerError is { } error)
            return new(HealthCheck.Server, error);

        var tailscaleOk = inputs.TailscaleEnabled && PairingAddress.HasAddress(ConnectionMode.Tailscale, inputs.Network);
        var lanOk = inputs.LanEnabled && PairingAddress.HasAddress(ConnectionMode.HomeWifi, inputs.Network);
        if (!tailscaleOk && !lanOk)
        {
            var problem = (inputs.TailscaleEnabled, inputs.LanEnabled) switch
            {
                (false, false) => "No connection set up. Use Pair a device",
                (true, false) => "Tailscale is not connected on this PC",
                (false, true) => "Home Wi-Fi: this PC is not on a Private network",
                (true, true) => "Tailscale is not connected and this network is not Private",
            };
            return new(HealthCheck.Address, problem);
        }

        return inputs.Firewall switch
        {
            FirewallState.MissingRule => new(HealthCheck.Firewall, "Windows Firewall rule is missing. Open Pair a device to fix it"),
            FirewallState.Blocked => new(HealthCheck.Firewall, "Windows Firewall is blocking Misty Step. Open Pair a device to fix it"),
            _ => HealthReport.Healthy,
        };
    }
}
