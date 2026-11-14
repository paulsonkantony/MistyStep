using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MistyStep.Core.Serialization;

namespace MistyStep.Core.Pairing;

/// <summary>What the Shortcut needs: base URL, device token, PC name.</summary>
public sealed record SetupInfo(
    [property: JsonPropertyName("u")] string Url,
    [property: JsonPropertyName("t")] string Token,
    [property: JsonPropertyName("n")] string PcName)
{
    // Never print the token.
    public override string ToString() => $"SetupInfo {{ Url = {Url}, PcName = {PcName} }}";
}

/// <summary>
/// <c>MS1.&lt;standard Base64 of UTF-8 JSON&gt;</c>. Standard Base64 (not base64url) because that is
/// what the Shortcuts decode action is expected to accept.
/// </summary>
public static class SetupCode
{
    public const string Prefix = "MS1.";

    public static string Encode(SetupInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var json = JsonSerializer.SerializeToUtf8Bytes(info, SetupJsonContext.Default.SetupInfo);
        return Prefix + Convert.ToBase64String(json);
    }

    public static bool TryDecode(string? code, out SetupInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim();
        if (!code.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        try
        {
            var json = Convert.FromBase64String(code[Prefix.Length..]);
            var decoded = JsonSerializer.Deserialize(json, SetupJsonContext.Default.SetupInfo);
            if (decoded is null
                || string.IsNullOrWhiteSpace(decoded.Url)
                || string.IsNullOrWhiteSpace(decoded.Token)
                || decoded.PcName is null
                || !Uri.TryCreate(decoded.Url, UriKind.Absolute, out _))
            {
                return false;
            }

            info = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return false;
        }
    }
}
