using System.Text.Json;

namespace MistyStep.Core.Network;

/// <summary>Reads the MagicDNS name from the output of <c>tailscale status --json</c> (<c>Self.DNSName</c>).</summary>
public static class TailscaleStatus
{
    /// <summary>The name without its trailing dot, or null if absent or not a plain DNS name.</summary>
    public static string? ReadDnsName(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Self", out var self)
                || self.ValueKind != JsonValueKind.Object
                || !self.TryGetProperty("DNSName", out var dnsName)
                || dnsName.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var name = PairingAddress.TrimDot(dnsName.GetString());
            return name is not null && IsDnsName(name) ? name : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Letters, digits, hyphens and dots only, at least two labels, none empty. Goes into a URL and a Host check.</summary>
    internal static bool IsDnsName(string name)
    {
        if (name.Length is 0 or > 253) return false;
        var labels = name.Split('.');
        if (labels.Length < 2) return false;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')) return false;
            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        }

        return true;
    }
}
