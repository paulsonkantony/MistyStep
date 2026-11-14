using System.Globalization;
using System.Text.Json;
using MistyStep.Core.Serialization;
using MistyStep.Core.Time;

namespace MistyStep.Core.Config;

public enum ConfigLoadStatus
{
    /// <summary>The file was read and is valid.</summary>
    Loaded,

    /// <summary>No file yet: first run.</summary>
    Missing,

    /// <summary>The file was unreadable and was renamed aside; devices must be paired again.</summary>
    RecoveredFromCorrupt,
}

public sealed record ConfigLoadResult(AppConfig Config, ConfigLoadStatus Status, string? BadFilePath = null);

/// <summary>Loads and atomically saves <c>config.json</c>.</summary>
public sealed class ConfigStore(string path, IClock clock)
{
    public string FilePath { get; } = Path.GetFullPath(path);

    public ConfigLoadResult Load()
    {
        if (!File.Exists(FilePath)) return new ConfigLoadResult(new AppConfig(), ConfigLoadStatus.Missing);

        AppConfig? config;
        try
        {
            var bytes = File.ReadAllBytes(FilePath);
            config = JsonSerializer.Deserialize(bytes, CoreJsonContext.Default.AppConfig);
            if (config is not null) config = Migrate(config);
        }
        catch (JsonException)
        {
            config = null;
        }

        if (config is not null && config.IsValid()) return new ConfigLoadResult(config, ConfigLoadStatus.Loaded);

        var badPath = MoveAside();
        return new ConfigLoadResult(new AppConfig(), ConfigLoadStatus.RecoveredFromCorrupt, badPath);
    }

    /// <summary>Writes to a temp file, flushes it to disk, then replaces the real file. Never leaves a half-written config.</summary>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, CoreJsonContext.Default.AppConfig);
        var tempPath = FilePath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, destinationBackupFileName: null);
        else File.Move(tempPath, FilePath);
    }

    /// <summary>Upgrades older schema versions. Version 1 is the only one so far.</summary>
    private static AppConfig Migrate(AppConfig config) => config;

    private string MoveAside()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        var stamp = clock.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var badPath = Path.Combine(directory, $"config.bad-{stamp}.json");
        for (var i = 1; File.Exists(badPath); i++)
            badPath = Path.Combine(directory, $"config.bad-{stamp}-{i}.json");

        File.Move(FilePath, badPath);
        return badPath;
    }
}

/// <summary>Where config and logs live: per-user folders, or beside the executable with <c>--portable</c>.</summary>
public sealed record AppPaths(string ConfigFile, string LogDirectory)
{
    public const string AppFolder = "MistyStep";

    public static AppPaths Resolve(bool portable, string exeDirectory)
    {
        if (portable)
            return new AppPaths(Path.Combine(exeDirectory, "config.json"), Path.Combine(exeDirectory, "logs"));

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(
            Path.Combine(roaming, AppFolder, "config.json"),
            Path.Combine(local, AppFolder, "logs"));
    }
}
