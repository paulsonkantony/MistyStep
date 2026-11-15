using System.Net;
using MistyStep.Core.Auth;
using MistyStep.Core.Network;
using MistyStep.Core.Store;
using MistyStep.Core.Time;

namespace MistyStep.Server;

/// <summary>Live values the server reads on every request. The app updates them as settings and the network change.</summary>
public interface IServerState
{
    int Port { get; }
    string PcName { get; }
    string AppVersion { get; }
    bool Paused { get; }
    bool TailscaleEnabled { get; }
    bool LanEnabled { get; }
    NetworkSnapshot Network { get; }

    /// <summary>iCloud link to the "Paste from PC" Shortcut, shown on the pairing page; null if not set.</summary>
    string? ShortcutUrl { get; }
}

/// <summary>A simple thread-safe <see cref="IServerState"/>: each property is a single atomic read or write.</summary>
public sealed class ServerState : IServerState
{
    private volatile NetworkSnapshot _network;
    private volatile string _pcName;
    private volatile string? _shortcutUrl;
    private volatile bool _paused;
    private volatile bool _tailscaleEnabled;
    private volatile bool _lanEnabled;
    private volatile int _port;

    public ServerState(int port, string pcName, string appVersion, NetworkSnapshot network)
    {
        Port = port;
        AppVersion = appVersion;
        _pcName = pcName;
        _network = network;
    }

    /// <summary>Settable so a port change can restart the server in place (devices must pair again).</summary>
    public int Port
    {
        get => _port;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 65535);
            _port = value;
        }
    }
    public string AppVersion { get; }

    public string PcName { get => _pcName; set => _pcName = value; }
    public bool Paused { get => _paused; set => _paused = value; }
    public bool TailscaleEnabled { get => _tailscaleEnabled; set => _tailscaleEnabled = value; }
    public bool LanEnabled { get => _lanEnabled; set => _lanEnabled = value; }
    public NetworkSnapshot Network { get => _network; set => _network = value; }
    public string? ShortcutUrl { get => _shortcutUrl; set => _shortcutUrl = value; }
}

/// <summary>Everything the server needs from the rest of the app.</summary>
public sealed record ServerDependencies(
    IClipStore Store,
    IDeviceRegistry Registry,
    AuthLockout Lockout,
    DeviceRateLimiter RateLimiter,
    IClock Clock,
    IServerState State)
{
    /// <summary>Address to bind. Production binds all IPv4 interfaces and filters by remote address.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
}
