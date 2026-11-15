using System.Globalization;
using System.Text.Json;

namespace MistyStep.Core.Settings;

/// <summary>Where the project lives. The update check and the About links use it.</summary>
public static class ProjectInfo
{
    public const string Repository = "paulsonkantony/MistyStep";
    public const string RepositoryUrl = "https://github.com/" + Repository;
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    /// <summary>GitHub's "latest release" API: published, non-draft, non-prerelease only.</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
}

/// <summary>A newer release the tray can point to.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Url);

/// <summary>
/// The opt-in weekly update check (spec, Packaging): read the latest release from the GitHub Releases API
/// and compare it with the running version. Nothing is downloaded or installed; the tray only links to it.
/// </summary>
public static class UpdateCheck
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    /// <summary>The release from a <c>releases/latest</c> response, or null if it is not a usable stable release.</summary>
    public static ReleaseInfo? ParseLatest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
            if (String(root, "tag_name") is not { } tag || !TryParseVersion(tag, out var version)) return null;

            // Only ever link to this project's own release pages.
            var url = String(root, "html_url");
            if (url is null || !url.StartsWith(ProjectInfo.ReleasesUrl + "/", StringComparison.Ordinal)) url = ProjectInfo.ReleasesUrl;
            return new ReleaseInfo(version!, tag, url);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Accepts <c>1.2.3</c> or <c>v1.2.3</c> (two to four numeric parts). Pre-release suffixes are rejected.</summary>
    public static bool TryParseVersion(string? text, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V')) trimmed = trimmed[1..];
        var parts = trimmed.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }

        version = numbers.Length switch
        {
            2 => new Version(numbers[0], numbers[1], 0),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
        return true;
    }

    /// <summary>True when <paramref name="release"/> is newer than the running version.</summary>
    public static bool IsNewer(ReleaseInfo release, string runningVersion)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!TryParseVersion(runningVersion, out var running)) return false;
        return Normalize(release.Version) > Normalize(running!);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
