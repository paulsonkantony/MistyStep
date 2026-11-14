using System.Text.Json;
using MistyStep.Core.Config;
using MistyStep.Core.Models;
using MistyStep.Core.Serialization;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Config;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mistystep-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();

    private string ConfigPath => Path.Combine(_directory, "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Defaults_match_the_settings_table()
    {
        var config = new AppConfig();
        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal(45654, config.Port);
        Assert.Equal(CaptureMode.Automatic, config.Mode);
        Assert.Equal("Ctrl+Alt+C", config.Hotkey);
        Assert.Equal(10, config.ExpiryMinutes);
        Assert.Null(config.TailscaleEnabled);
        Assert.False(config.LanEnabled);
        Assert.Equal(1_048_576, config.MaxTextBytes);
        Assert.Equal(26_214_400, config.MaxImageBytes);
        Assert.Equal(4096, config.MaxImageEdge);
        Assert.True(config.HonorSecrecyMarkers);
        Assert.Equal(["KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe"], config.ExcludedApps);
        Assert.True(config.ServeWhileLocked);
        Assert.False(config.NotifyOnFetch);
        Assert.False(config.UpdateCheck);
        Assert.Null(config.ShortcutUrl);
        Assert.Empty(config.Devices);
    }

    [Fact]
    public void Json_uses_camel_case_and_lowercase_mode()
    {
        var json = JsonSerializer.Serialize(new AppConfig { Mode = CaptureMode.Manual }, CoreJsonContext.Default.AppConfig);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\": \"manual\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maxTextBytes\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var result = new ConfigStore(ConfigPath, _clock).Load();
        Assert.Equal(ConfigLoadStatus.Missing, result.Status);
        Assert.Equal(new AppConfig().Port, result.Config.Port);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new ConfigStore(ConfigPath, _clock);
        var device = new DeviceRecord("d_4f2a9c", "iPad", new string('c', 64),
            "http://my-laptop.tailnet-name.ts.net:45654",
            new DateTime(2026, 11, 14, 19, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 14, 19, 42, 10, DateTimeKind.Utc), 17);
        var config = new AppConfig
        {
            Port = 50000,
            Mode = CaptureMode.Manual,
            ExpiryMinutes = 0,
            TailscaleEnabled = true,
            LanEnabled = true,
            ExcludedApps = ["Foo.exe"],
            Devices = [device],
        };

        store.Save(config);
        var result = store.Load();

        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal(50000, result.Config.Port);
        Assert.Equal(CaptureMode.Manual, result.Config.Mode);
        Assert.Equal(0, result.Config.ExpiryMinutes);
        Assert.True(result.Config.TailscaleEnabled);
        Assert.True(result.Config.LanEnabled);
        Assert.Equal(["Foo.exe"], result.Config.ExcludedApps);
        Assert.Equal(device, Assert.Single(result.Config.Devices));

        var json = File.ReadAllText(ConfigPath);
        Assert.Contains("\"createdUtc\": \"2026-11-14T19:30:00Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_replaces_existing_file_and_leaves_no_temp_file()
    {
        var store = new ConfigStore(ConfigPath, _clock);
        store.Save(new AppConfig { Port = 1111 });
        store.Save(new AppConfig { Port = 2222 });

        Assert.Equal(2222, store.Load().Config.Port);
        Assert.Equal(["config.json"], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_overwrites_a_stale_temp_file()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath + ".tmp", "half-written garbage");
        var store = new ConfigStore(ConfigPath, _clock);
        store.Save(new AppConfig { Port = 3333 });

        Assert.Equal(3333, store.Load().Config.Port);
        Assert.False(File.Exists(ConfigPath + ".tmp"));
    }

    [Fact]
    public void Interrupted_write_leaves_the_previous_config_intact()
    {
        var store = new ConfigStore(ConfigPath, _clock);
        store.Save(new AppConfig { Port = 4444 });

        // Simulate a crash after the temp file was written but before it replaced the real one.
        File.WriteAllText(ConfigPath + ".tmp", "{ \"port\": ");
        Assert.Equal(4444, store.Load().Config.Port);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{ "schemaVersion": 99 }""")]
    [InlineData("""{ "schemaVersion": 0 }""")]
    [InlineData("""{ "port": 70000 }""")]
    [InlineData("""{ "expiryMinutes": -1 }""")]
    [InlineData("""{ "mode": "sometimes" }""")]
    [InlineData("""{ "devices": [ { "id": "d_1", "name": "x", "tokenHash": "short", "baseUrl": "http://a:1", "createdUtc": "2026-01-01T00:00:00Z", "fetchCount": 0 } ] }""")]
    public void Corrupt_file_is_renamed_and_defaults_are_used(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, content);

        var result = new ConfigStore(ConfigPath, _clock).Load();

        Assert.Equal(ConfigLoadStatus.RecoveredFromCorrupt, result.Status);
        Assert.Equal(new AppConfig().Port, result.Config.Port);
        Assert.Empty(result.Config.Devices);
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal(Path.Combine(_directory, "config.bad-20261114120000.json"), result.BadFilePath);
        Assert.Equal(content, File.ReadAllText(result.BadFilePath!));
    }

    [Fact]
    public void Second_corruption_in_the_same_second_gets_a_unique_name()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConfigStore(ConfigPath, _clock);

        File.WriteAllText(ConfigPath, "bad1");
        var first = store.Load().BadFilePath;
        File.WriteAllText(ConfigPath, "bad2");
        var second = store.Load().BadFilePath;

        Assert.NotEqual(first, second);
        Assert.Equal("bad1", File.ReadAllText(first!));
        Assert.Equal("bad2", File.ReadAllText(second!));
    }

    [Fact]
    public void Unknown_keys_and_comments_are_tolerated()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, """
            {
              // hand edited
              "port": 45655,
              "someFutureKey": true,
            }
            """);
        var result = new ConfigStore(ConfigPath, _clock).Load();
        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal(45655, result.Config.Port);
    }

    [Fact]
    public void Keys_missing_from_the_file_keep_their_defaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, """{ "schemaVersion": 1 }""");
        var result = new ConfigStore(ConfigPath, _clock).Load();

        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal("Ctrl+Alt+C", result.Config.Hotkey);
        Assert.Equal(10, result.Config.ExpiryMinutes);
        Assert.Equal(4, result.Config.ExcludedApps.Count);
        Assert.True(result.Config.HonorSecrecyMarkers);
        Assert.Empty(result.Config.Devices);
    }

    [Fact]
    public void Portable_paths_sit_beside_the_executable()
    {
        var paths = AppPaths.Resolve(portable: true, exeDirectory: _directory);
        Assert.Equal(Path.Combine(_directory, "config.json"), paths.ConfigFile);
        Assert.Equal(Path.Combine(_directory, "logs"), paths.LogDirectory);
    }

    [Fact]
    public void Installed_paths_use_per_user_folders()
    {
        var paths = AppPaths.Resolve(portable: false, exeDirectory: _directory);
        Assert.EndsWith(Path.Combine("MistyStep", "config.json"), paths.ConfigFile);
        Assert.EndsWith(Path.Combine("MistyStep", "logs"), paths.LogDirectory);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), paths.ConfigFile);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), paths.LogDirectory);
    }
}
