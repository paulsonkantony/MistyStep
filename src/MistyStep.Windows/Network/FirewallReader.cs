using System.Collections;
using System.Runtime.InteropServices;
using MistyStep.Core.Network;

namespace MistyStep.Windows.Network;

/// <summary>
/// Reads Windows Firewall state through INetFwPolicy2 (HNetCfg.FwPolicy2), which needs no elevation.
/// Only rule metadata is read; nothing is changed here.
/// </summary>
public static class FirewallReader
{
    /// <summary>Firewall state for this program and port, or <see cref="FirewallState.Unknown"/> if it cannot be read.</summary>
    public static FirewallState Check(string exePath, int port, bool needTailscale, bool needLan)
    {
        if (!needTailscale && !needLan) return FirewallState.Ok;
        var snapshot = Read();
        return snapshot is null
            ? FirewallState.Unknown
            : FirewallAssessment.Assess(snapshot.Value.Rules, snapshot.Value.EnabledProfiles, exePath, port, needTailscale, needLan);
    }

    internal static (IReadOnlyList<FirewallRule> Rules, FirewallProfiles EnabledProfiles)? Read()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false);
        if (type is null) return null;

        object? policyObject = null;
        try
        {
            policyObject = Activator.CreateInstance(type);
            if (policyObject is null) return null;
            dynamic policy = policyObject;

            var enabled = FirewallProfiles.None;
            foreach (var profile in new[] { FirewallProfiles.Domain, FirewallProfiles.Private, FirewallProfiles.Public })
            {
                bool on;
                try { on = (bool)policy.FirewallEnabled[(int)profile]; }
                catch (Exception ex) when (IsComFailure(ex)) { on = true; }
                if (on) enabled |= profile;
            }

            var rules = new List<FirewallRule>();
            foreach (dynamic rule in (IEnumerable)policy.Rules)
            {
                try
                {
                    rules.Add(new FirewallRule(
                        Name: (string?)rule.Name ?? "",
                        Enabled: (bool)rule.Enabled,
                        Inbound: (int)rule.Direction == 1,
                        Allow: (int)rule.Action == 1,
                        ApplicationName: (string?)rule.ApplicationName,
                        LocalPorts: (string?)rule.LocalPorts,
                        Protocol: (int)rule.Protocol,
                        Profiles: (FirewallProfiles)((int)rule.Profiles & (int)FirewallProfiles.All)));
                }
                catch (Exception ex) when (IsComFailure(ex))
                {
                    // Skip a rule that cannot be read.
                }
                finally
                {
                    if (Marshal.IsComObject((object)rule)) Marshal.ReleaseComObject((object)rule);
                }
            }

            return (rules, enabled);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return null;
        }
        finally
        {
            if (policyObject is not null && Marshal.IsComObject(policyObject)) Marshal.ReleaseComObject(policyObject);
        }
    }

    private static bool IsComFailure(Exception ex) =>
        ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
            or UnauthorizedAccessException or NullReferenceException or InvalidComObjectException;
}
