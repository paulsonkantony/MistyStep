using System.Net.NetworkInformation;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MistyStep.App.Logging;
using MistyStep.Core.Auth;
using MistyStep.Core.Capture;
using MistyStep.Core.Config;
using MistyStep.Core.Network;
using MistyStep.Core.Store;
using MistyStep.Core.Time;
using MistyStep.Server;
using MistyStep.Windows.Network;

namespace MistyStep.App;

/// <summary>
/// Composition root: settings, clip store, device registry, network state and the HTTP server.
/// Created on the UI thread before the message loop starts (so no synchronization context is captured
/// by the server start-up).
/// </summary>
internal sealed class AppHost : IAsyncDisposable
{
    private readonly RollingFileLoggerProvider _fileLogs;

    private AppHost(RollingFileLoggerProvider fileLogs, ILoggerFactory loggerFactory)
    {
        _fileLogs = fileLogs;
        LoggerFactory = loggerFactory;
        Log = loggerFactory.CreateLogger("MistyStep");
    }

    public ILoggerFactory LoggerFactory { get; }
    public ILogger Log { get; }
    public IClock Clock { get; } = SystemClock.Instance;
    public string Version { get; } = ReadVersion();
    public ConfigLoadResult LoadResult { get; private set; } = null!;
    public ConfigPersistence Persistence { get; private set; } = null!;
    public ClipStore Store { get; private set; } = null!;
    public DeviceRegistry Registry { get; private set; } = null!;
    public ServerState State { get; private set; } = null!;
    public ServerHost? Server { get; private set; }

    /// <summary>Why the server is not running (shown in the tray), or null when it is.</summary>
    public string? ServerError { get; private set; }

    public static AppHost Start(AppOptions options)
    {
        var paths = AppPaths.Resolve(options.Portable, AppContext.BaseDirectory);
        var level = options.DebugLogging ? LogLevel.Debug : LogLevel.Information;
        var fileLogs = new RollingFileLoggerProvider(paths.LogDirectory);
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => ConfigureLogging(builder, fileLogs, level));

        var host = new AppHost(fileLogs, loggerFactory);
        host.Initialize(paths, fileLogs, level);
        return host;
    }

    private void Initialize(AppPaths paths, RollingFileLoggerProvider fileLogs, LogLevel level)
    {
        Log.LogInformation("Starting Misty Step {Version}", Version);

        var configStore = new ConfigStore(paths.ConfigFile, Clock);
        LoadResult = configStore.Load();
        if (LoadResult.Status == ConfigLoadStatus.RecoveredFromCorrupt)
            Log.LogWarning("Settings file was unreadable and was renamed aside; starting with defaults");
        var config = LoadResult.Config;

        Registry = new DeviceRegistry(Clock);
        Registry.Load(config.Devices);
        Persistence = new ConfigPersistence(configStore, Registry, Clock, config);
        Persistence.SaveFailed += error => Log.LogWarning("Could not save settings: {ErrorType}", error);
        Store = new ClipStore(Clock, config.ExpiryMinutes);

        var network = NetworkProbe.Probe(magicDnsName: null);
        State = new ServerState(config.Port, Environment.MachineName, Version, network)
        {
            TailscaleEnabled = config.TailscaleEnabled ?? network.TailscaleAddress is not null,
            LanEnabled = config.LanEnabled,
            ShortcutUrl = config.ShortcutUrl,
        };
        Log.LogInformation("Tailscale address {Found}; Home Wi-Fi {Lan}",
            network.TailscaleAddress is null ? "not found" : "found",
            PairingAddress.HasAddress(ConnectionMode.HomeWifi, network) ? "available" : "not available (no Private network)");

        var deps = new ServerDependencies(Store, Registry, new AuthLockout(Clock), new DeviceRateLimiter(Clock), Clock, State);
        try
        {
            Server = ServerHost.StartAsync(deps, builder =>
            {
                builder.AddProvider(fileLogs);
                builder.SetMinimumLevel(level);
            }).GetAwaiter().GetResult();
            Log.LogInformation("Listening on port {Port}", config.Port);
        }
        catch (PortInUseException)
        {
            ServerError = $"Port {config.Port} is already in use";
            Log.LogError("Port {Port} is already in use", config.Port);
        }
        catch (Exception ex)
        {
            ServerError = "The sharing server could not start";
            Log.LogError(ex, "Server failed to start");
        }

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // The MagicDNS name and firewall state take a process run and a COM walk: fetch them off the UI thread.
        RefreshInBackground(firewall: true);
    }

    /// <summary>The running executable; the firewall rules are scoped to it.</summary>
    public string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MistyStep.exe");

    /// <summary>Last firewall check. Unknown until the first check finishes, and never reported as a problem.</summary>
    public FirewallState Firewall { get; private set; } = FirewallState.Unknown;

    /// <summary>Raised (on a pool thread) after the network snapshot or firewall state changed.</summary>
    public event Action? NetworkChanged;

    /// <summary>The four health checks. The clipboard listener lives in the tray, so it is passed in.</summary>
    public HealthReport EvaluateHealth(bool listenerRegistered) =>
        Health.Evaluate(new HealthInputs(listenerRegistered, ServerError, State.TailscaleEnabled, State.LanEnabled, State.Network, Firewall));

    /// <summary>Paired devices whose stored address no longer reaches this PC.</summary>
    public IReadOnlyList<Device> StaleDevices() => DeviceAddressCheck.StaleDevices(Registry.Devices, State.Network, State.Port);

    /// <summary>Turns Home Wi-Fi mode on (the user opts in during pairing).</summary>
    public void EnableLan()
    {
        State.LanEnabled = true;
        if (!Persistence.Settings.LanEnabled) Persistence.Update(c => c with { LanEnabled = true });
        Log.LogInformation("Home Wi-Fi mode turned on");
    }

    /// <summary>Turns Tailscale mode on (pairing over Tailscale turns it on for good).</summary>
    public void EnableTailscale()
    {
        State.TailscaleEnabled = true;
        if (Persistence.Settings.TailscaleEnabled != true) Persistence.Update(c => c with { TailscaleEnabled = true });
    }

    /// <summary>Re-reads the firewall rules for the enabled modes (plus <paramref name="extraMode"/>, for a wizard preflight).</summary>
    public FirewallState RefreshFirewall(ConnectionMode? extraMode = null)
    {
        var needTailscale = State.TailscaleEnabled || extraMode == ConnectionMode.Tailscale;
        var needLan = State.LanEnabled || extraMode == ConnectionMode.HomeWifi;
        FirewallState state;
        try { state = FirewallReader.Check(ExePath, State.Port, needTailscale, needLan); }
        catch (Exception ex)
        {
            Log.LogWarning("Firewall check failed: {ErrorType}", ex.GetType().Name);
            state = FirewallState.Unknown;
        }

        if (extraMode is null || (State.TailscaleEnabled == needTailscale && State.LanEnabled == needLan))
        {
            if (state != Firewall) Log.LogInformation("Firewall state {State}", state);
            Firewall = state;
        }

        return state;
    }

    /// <summary>Probes the network (and, when asked, the firewall) on a pool thread, then raises <see cref="NetworkChanged"/>.</summary>
    public void RefreshInBackground(bool firewall) => Task.Run(async () =>
    {
        try
        {
            await RefreshNetworkAsync(CancellationToken.None).ConfigureAwait(false);
            if (firewall) RefreshFirewall();
            NetworkChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.LogWarning("Network refresh failed: {ErrorType}", ex.GetType().Name);
        }
    });

    /// <summary>Settings that shape capture, read fresh for every copy.</summary>
    public CaptureLimits CaptureLimits
    {
        get
        {
            var c = Persistence.Settings;
            return new CaptureLimits
            {
                MaxTextBytes = c.MaxTextBytes,
                MaxImageBytes = c.MaxImageBytes,
                MaxImageEdge = c.MaxImageEdge,
                HonorSecrecyMarkers = c.HonorSecrecyMarkers,
                ExcludedApps = c.ExcludedApps,
            };
        }
    }

    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private string? _magicDnsName;
    private System.Net.IPAddress? _magicDnsFor;

    /// <summary>
    /// Re-reads local addresses and categories. Tailscale often comes up after the app at sign-in, and Windows
    /// does not signal a Private/Public switch, so the tray also calls this every 15 s. The MagicDNS name is
    /// fetched again only when the Tailscale address changes.
    /// </summary>
    public async Task RefreshNetworkAsync(CancellationToken cancellationToken)
    {
        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = State.Network;
            var snapshot = NetworkProbe.Probe(_magicDnsName);

            if (snapshot.TailscaleAddress is not null && !snapshot.TailscaleAddress.Equals(_magicDnsFor))
            {
                _magicDnsName = await TailscaleCli.GetMagicDnsNameAsync(cancellationToken).ConfigureAwait(false);
                _magicDnsFor = snapshot.TailscaleAddress;
                snapshot = snapshot with { MagicDnsName = _magicDnsName };
                Log.LogInformation("Tailscale name {Found}", _magicDnsName is null ? "not found; using the address" : "found");
            }

            State.Network = snapshot;
            if (Persistence.Settings.TailscaleEnabled is null && snapshot.TailscaleAddress is not null) State.TailscaleEnabled = true;

            if ((before.TailscaleAddress is null) != (snapshot.TailscaleAddress is null))
                Log.LogInformation("Tailscale address {Found}", snapshot.TailscaleAddress is null ? "lost" : "found");
            var lanBefore = PairingAddress.HasAddress(ConnectionMode.HomeWifi, before);
            var lanNow = PairingAddress.HasAddress(ConnectionMode.HomeWifi, snapshot);
            if (lanBefore != lanNow)
                Log.LogInformation("Home Wi-Fi {Lan}", lanNow ? "available (Private network)" : "not available (no Private network)");
        }
        finally
        {
            _probeLock.Release();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => RefreshInBackground(firewall: true);

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Log.LogInformation("Resumed from sleep");
        RefreshInBackground(firewall: true);
    }

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Persistence?.Flush();
        Persistence?.Dispose();
        if (Server is not null)
        {
            try { await Server.DisposeAsync(); }
            catch (Exception ex) { Log.LogWarning("Server shutdown: {ErrorType}", ex.GetType().Name); }
        }

        Log.LogInformation("Stopped");
        LoggerFactory.Dispose();
        _fileLogs.Dispose();
    }

    internal static void ConfigureLogging(ILoggingBuilder builder, ILoggerProvider provider, LogLevel level)
    {
        builder.AddProvider(provider);
        builder.SetMinimumLevel(level);
        builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
    }

    private static string ReadVersion()
    {
        var informational = typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
