using MistyStep.Core.Auth;
using MistyStep.Core.Time;

namespace MistyStep.Core.Config;

/// <summary>
/// Keeps <c>config.json</c> in step with the settings and the device registry. Structural device
/// changes (pair, rename, revoke) and setting changes are saved at once; activity (last seen, fetch
/// count) is batched to at most one write per minute. Thread-safe: the registry raises changes on
/// server threads.
/// </summary>
public sealed class ConfigPersistence : IDisposable
{
    public const long ActivitySaveIntervalMs = 60_000;

    private readonly ConfigStore _store;
    private readonly IDeviceRegistry _registry;
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private AppConfig _settings;
    private bool _dirty;
    private long _lastSaveMs;

    public ConfigPersistence(ConfigStore store, IDeviceRegistry registry, IClock clock, AppConfig initial)
    {
        _store = store;
        _registry = registry;
        _clock = clock;
        _settings = initial;
        _lastSaveMs = clock.MonotonicMs - ActivitySaveIntervalMs;
        _registry.Changed += OnRegistryChanged;
    }

    /// <summary>Raised with the exception type when a save fails (the next change retries).</summary>
    public event Action<string>? SaveFailed;

    /// <summary>The current settings. <see cref="AppConfig.Devices"/> is filled from the registry on save.</summary>
    public AppConfig Settings
    {
        get { lock (_gate) return _settings; }
    }

    /// <summary>Applies a settings change and saves at once.</summary>
    public void Update(Func<AppConfig, AppConfig> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            _settings = change(_settings);
            SaveLocked();
        }
    }

    /// <summary>Call periodically (e.g. every 15 s): writes batched activity once a minute has passed.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_dirty && _clock.MonotonicMs - _lastSaveMs >= ActivitySaveIntervalMs) SaveLocked();
        }
    }

    /// <summary>Writes any pending changes now, e.g. on exit.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_dirty) SaveLocked();
        }
    }

    public void Dispose() => _registry.Changed -= OnRegistryChanged;

    private void OnRegistryChanged(RegistryChange change)
    {
        lock (_gate)
        {
            if (change == RegistryChange.Structural) SaveLocked();
            else _dirty = true;
        }
    }

    private void SaveLocked()
    {
        try
        {
            _store.Save(_settings with { Devices = _registry.ToRecords() });
            _dirty = false;
            _lastSaveMs = _clock.MonotonicMs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dirty = true;
            SaveFailed?.Invoke(ex.GetType().Name);
        }
    }
}
