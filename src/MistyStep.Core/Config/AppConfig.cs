using MistyStep.Core.Models;

namespace MistyStep.Core.Config;

/// <summary>
/// The per-user settings file. Holds token hashes, never tokens. "Start with Windows" is not here:
/// the HKCU Run value is the single source of truth.
/// </summary>
public sealed record AppConfig
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultPort = 45654;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int Port { get; set; } = DefaultPort;
    public CaptureMode Mode { get; set; } = CaptureMode.Automatic;
    public string Hotkey { get; set; } = "Ctrl+Alt+C";

    /// <summary>0 means until replaced.</summary>
    public int ExpiryMinutes { get; set; } = 10;

    /// <summary>Null until first run decides (true when Tailscale is detected).</summary>
    public bool? TailscaleEnabled { get; set; }

    public bool LanEnabled { get; set; }
    public int MaxTextBytes { get; set; } = 1_048_576;
    public long MaxImageBytes { get; set; } = 26_214_400;
    public int MaxImageEdge { get; set; } = 4096;
    public bool HonorSecrecyMarkers { get; set; } = true;

    public IReadOnlyList<string> ExcludedApps { get; set; } =
        ["KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe"];

    public bool ServeWhileLocked { get; set; } = true;
    public bool NotifyOnFetch { get; set; }
    public bool UpdateCheck { get; set; }

    /// <summary>Null means the built-in iCloud link. Set to override for forks.</summary>
    public string? ShortcutUrl { get; set; }

    public IReadOnlyList<DeviceRecord> Devices { get; set; } = [];

    /// <summary>Basic range checks. A file that fails them is treated as corrupt.</summary>
    internal bool IsValid() =>
        SchemaVersion is >= 1 and <= CurrentSchemaVersion
        && Port is >= 1 and <= 65535
        && ExpiryMinutes >= 0
        && MaxTextBytes > 0
        && MaxImageBytes > 0
        && MaxImageEdge > 0
        && Enum.IsDefined(Mode)
        && Hotkey is not null
        && ExcludedApps is not null
        && Devices is not null
        && Devices.All(d => d is not null && d.IsValid());
}

/// <summary>A paired device as stored on disk.</summary>
public sealed record DeviceRecord(
    string Id,
    string Name,
    string TokenHash,
    string BaseUrl,
    DateTime CreatedUtc,
    DateTime? LastSeenUtc,
    long FetchCount)
{
    internal bool IsValid() =>
        !string.IsNullOrWhiteSpace(Id)
        && Name is not null
        && TokenHash is { Length: 64 }
        && TokenHash.All(char.IsAsciiHexDigit)
        && BaseUrl is not null
        && FetchCount >= 0;
}
