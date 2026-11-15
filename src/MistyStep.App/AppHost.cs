using System.Net.NetworkInformation;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MistyStep.App.Logging;
using MistyStep.Core.Auth;
using MistyStep.Core.Capture;
using MistyStep.Core.Config;
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

        var network = NetworkProbe.Probe();
        State = new ServerState(config.Port, Environment.MachineName, Version, network)
        {
            TailscaleEnabled = config.TailscaleEnabled ?? network.TailscaleAddress is not null,
            LanEnabled = config.LanEnabled,
            ShortcutUrl = config.ShortcutUrl,
        };
        Log.LogInformation("Tailscale address {Found}", network.TailscaleAddress is null ? "not found" : "found");

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
    }

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

    /// <summary>Re-reads local addresses. Tailscale often comes up after the app at sign-in.</summary>
    public void RefreshNetwork()
    {
        var snapshot = NetworkProbe.Probe();
        var hadTailscale = State.Network.TailscaleAddress is not null;
        State.Network = snapshot;
        if (Persistence.Settings.TailscaleEnabled is null && snapshot.TailscaleAddress is not null) State.TailscaleEnabled = true;
        if (hadTailscale != snapshot.TailscaleAddress is not null)
            Log.LogInformation("Tailscale address {Found}", snapshot.TailscaleAddress is null ? "lost" : "found");
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Task.Run(() =>
    {
        try { RefreshNetwork(); }
        catch (Exception ex) { Log.LogWarning("Network probe failed: {ErrorType}", ex.GetType().Name); }
    });

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
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
