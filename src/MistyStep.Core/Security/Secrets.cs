using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MistyStep.Core.Security;

/// <summary>All random values and token hashing. Every random byte comes from <see cref="RandomNumberGenerator"/>.</summary>
public static class Secrets
{
    public const int TokenBytes = 32;

    /// <summary>Lower-case hex of <paramref name="byteCount"/> random bytes.</summary>
    public static string NewHexId(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        Span<byte> buffer = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexStringLower(buffer);
    }

    /// <summary>A device token: 32 random bytes as base64url without padding (43 characters).</summary>
    public static string NewToken()
    {
        Span<byte> buffer = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(buffer);
        return Base64Url.EncodeToString(buffer);
    }

    /// <summary>SHA-256 of the UTF-8 bytes of <paramref name="value"/>.</summary>
    public static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    /// <summary>Lower-case hex SHA-256 of the UTF-8 bytes of <paramref name="value"/> (64 characters).</summary>
    public static string Sha256Hex(string value) => Convert.ToHexStringLower(Sha256(value));
}
