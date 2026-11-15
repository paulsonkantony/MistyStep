using System.Net;
using MistyStep.Core.Auth;
using MistyStep.Core.Config;
using MistyStep.Core.Models;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Config;

public sealed class ConfigPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mistystep-persist-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly DeviceRegistry _registry;
    private readonly ConfigStore _store;

    public ConfigPersistenceTests()
    {
        _registry = new DeviceRegistry(_clock);
        _store = new ConfigStore(Path.Combine(_directory, "config.json"), _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string PairDevice()
    {
        var code = _registry.CreatePairingCode("http://100.101.102.103:45654", "PC");
        var redeemed = (RedeemResult.Redeemed)_registry.Redeem(code.Code, IPAddress.Parse("100.90.1.2"), null);
        return redeemed.Device.Id;
    }

    private DateTime? SavedLastSeen() => _store.Load().Config.Devices.SingleOrDefault()?.LastSeenUtc;

    [Fact]
    public void Pairing_is_saved_immediately()
    {
        using var persistence = new ConfigPersistence(_store, _registry, _clock, new AppConfig());
        var id = PairDevice();
        Assert.Equal(id, Assert.Single(_store.Load().Config.Devices).Id);
    }

    [Fact]
    public void Revoke_and_rename_are_saved_immediately()
    {
        using var persistence = new ConfigPersistence(_store, _registry, _clock, new AppConfig());
        var id = PairDevice();
        _registry.Rename(id, "Kitchen iPad");
        Assert.Equal("Kitchen iPad", _store.Load().Config.Devices.Single().Name);
        _registry.Revoke(id);
        Assert.Empty(_store.Load().Config.Devices);
    }

    [Fact]
    public void Activity_is_batched_to_once_a_minute()
    {
        using var persistence = new ConfigPersistence(_store, _registry, _clock, new AppConfig());
        var id = PairDevice(); // saved now

        _registry.RecordSeen(id);
        persistence.Tick();
        Assert.Null(SavedLastSeen()); // less than a minute since the pairing save

        _clock.Advance(59_999);
        persistence.Tick();
        Assert.Null(SavedLastSeen());

        _clock.Advance(1);
        persistence.Tick();
        Assert.NotNull(SavedLastSeen());
    }

    [Fact]
    public void Tick_without_changes_does_not_write()
    {
        using var persistence = new ConfigPersistence(_store, _registry, _clock, new AppConfig());
        _clock.Advance(120_000);
        persistence.Tick();
        Assert.False(File.Exists(_store.FilePath));
    }

    [Fact]
    public void Flush_writes_pending_activity()
    {
        using var persistence = new ConfigPersistence(_store, _registry, _clock, new AppConfig());
        var id = PairDevice();
        _registry.RecordDelivery(id, "item");
        persistence.Flush();
        Assert.Equal(1, _store.Load().Config.Devices.Single().FetchCount);
    }

    [Fact]
    public void Settings_update_is_saved_with_devices()
    {
        using var persistence = new ConfigPersistence(_store, _registry, _clock, new AppConfig());
        PairDevice();
        persistence.Update(c => c with { Mode = CaptureMode.Manual, TailscaleEnabled = true });

        var saved = _store.Load().Config;
        Assert.Equal(CaptureMode.Manual, saved.Mode);
        Assert.True(saved.TailscaleEnabled);
        Assert.Single(saved.Devices);
        Assert.Equal(CaptureMode.Manual, persistence.Settings.Mode);
    }

    [Fact]
    public void Dispose_stops_listening()
    {
        var persistence = new ConfigPersistence(_store, _registry, _clock, new AppConfig());
        persistence.Dispose();
        PairDevice();
        Assert.False(File.Exists(_store.FilePath));
    }
}
