using System.Globalization;

namespace MistyStep.Core.Network;

/// <summary>Windows Firewall profiles, as in NET_FW_PROFILE_TYPE2.</summary>
[Flags]
public enum FirewallProfiles
{
    None = 0,
    Domain = 1,
    Private = 2,
    Public = 4,
    All = Domain | Private | Public,
}

/// <summary>The firewall part of the health status.</summary>
public enum FirewallState
{
    /// <summary>The rules each enabled mode needs are present, or the firewall is off.</summary>
    Ok,

    /// <summary>A needed "Misty Step (...)" rule is missing, disabled or for another port or program.</summary>
    MissingRule,

    /// <summary>An inbound block rule names this program (usually from cancelling Windows' firewall prompt).</summary>
    Blocked,

    /// <summary>The rules could not be read. Not reported as a problem.</summary>
    Unknown,
}

/// <summary>One inbound or outbound rule, as read from INetFwPolicy2.</summary>
public sealed record FirewallRule(
    string Name,
    bool Enabled,
    bool Inbound,
    bool Allow,
    string? ApplicationName,
    string? LocalPorts,
    int Protocol,
    FirewallProfiles Profiles);

/// <summary>Decides whether Windows Firewall lets paired devices in (spec, Firewall rules).</summary>
public static class FirewallAssessment
{
    public const string TailscaleRuleName = "Misty Step (Tailscale)";
    public const string HomeWifiRuleName = "Misty Step (Home Wi-Fi)";
    public const int TcpProtocol = 6;

    public static FirewallState Assess(
        IEnumerable<FirewallRule> rules,
        FirewallProfiles enabledProfiles,
        string exePath,
        int port,
        bool needTailscale,
        bool needLan)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrEmpty(exePath);

        // Home Wi-Fi only serves Private networks, so its rule matters only when that profile's firewall is on.
        needLan &= enabledProfiles.HasFlag(FirewallProfiles.Private);
        needTailscale &= enabledProfiles != FirewallProfiles.None;
        if (!needTailscale && !needLan) return FirewallState.Ok;

        var list = rules as IReadOnlyCollection<FirewallRule> ?? rules.ToList();

        // A block rule wins over any allow rule.
        if (list.Any(r => r.Enabled && r.Inbound && !r.Allow && SameProgram(r.ApplicationName, exePath) && (r.Profiles & enabledProfiles) != 0))
            return FirewallState.Blocked;

        if (needTailscale && !HasRule(list, TailscaleRuleName, exePath, port, FirewallProfiles.None)) return FirewallState.MissingRule;
        if (needLan && !HasRule(list, HomeWifiRuleName, exePath, port, FirewallProfiles.Private)) return FirewallState.MissingRule;
        return FirewallState.Ok;
    }

    private static bool HasRule(IEnumerable<FirewallRule> rules, string name, string exePath, int port, FirewallProfiles requiredProfile) =>
        rules.Any(r =>
            string.Equals(r.Name, name, StringComparison.Ordinal)
            && r.Enabled
            && r.Inbound
            && r.Allow
            && r.Protocol == TcpProtocol
            && SameProgram(r.ApplicationName, exePath)
            && string.Equals(r.LocalPorts?.Trim(), port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && (requiredProfile == FirewallProfiles.None || r.Profiles.HasFlag(requiredProfile)));

    internal static bool SameProgram(string? ruleProgram, string exePath)
    {
        if (string.IsNullOrWhiteSpace(ruleProgram)) return false;
        return string.Equals(Normalize(ruleProgram), Normalize(exePath), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        var trimmed = path.Trim().Trim('"');
        try { return Path.GetFullPath(trimmed); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return trimmed; }
    }
}
