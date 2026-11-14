using System.Net;
using MistyStep.Core.Time;

namespace MistyStep.Core.Auth;

/// <summary>Five failed authentications from one address within 60 s lock it out for 5 minutes.</summary>
public sealed class AuthLockout(IClock clock)
{
    public const int MaxFailures = 5;
    public const long WindowMs = 60_000;
    public const long LockoutMs = 5 * 60_000;
    public const int MaxEntries = 1000;

    private sealed class Entry
    {
        public readonly Queue<long> Failures = new();
        public long LockedUntilMs;
        public long LastTouchedMs;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, Entry> _entries = new();

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public bool IsLockedOut(IPAddress remote)
    {
        remote = Normalize(remote);
        lock (_gate) return _entries.TryGetValue(remote, out var entry) && clock.MonotonicMs < entry.LockedUntilMs;
    }

    public void RecordFailure(IPAddress remote)
    {
        remote = Normalize(remote);
        var now = clock.MonotonicMs;
        lock (_gate)
        {
            if (!_entries.TryGetValue(remote, out var entry))
            {
                if (_entries.Count >= MaxEntries) EvictOldest();
                entry = new Entry();
                _entries[remote] = entry;
            }

            entry.LastTouchedMs = now;
            while (entry.Failures.Count > 0 && now - entry.Failures.Peek() >= WindowMs) entry.Failures.Dequeue();
            entry.Failures.Enqueue(now);

            if (entry.Failures.Count >= MaxFailures)
            {
                entry.LockedUntilMs = now + LockoutMs;
                entry.Failures.Clear();
            }
        }
    }

    private void EvictOldest()
    {
        IPAddress? oldest = null;
        var oldestTime = long.MaxValue;
        foreach (var (address, entry) in _entries)
        {
            if (entry.LastTouchedMs < oldestTime)
            {
                oldestTime = entry.LastTouchedMs;
                oldest = address;
            }
        }

        if (oldest is not null) _entries.Remove(oldest);
    }

    private static IPAddress Normalize(IPAddress remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        return remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
    }
}

/// <summary>At most 20 requests per second per device; answer 429 above that.</summary>
public sealed class DeviceRateLimiter(IClock clock)
{
    public const int MaxRequestsPerSecond = 20;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (long WindowStartMs, int Count)> _windows = new(StringComparer.Ordinal);

    public bool TryAcquire(string deviceId)
    {
        var now = clock.MonotonicMs;
        lock (_gate)
        {
            if (!_windows.TryGetValue(deviceId, out var window) || now - window.WindowStartMs >= 1000)
                window = (now, 0);

            if (window.Count >= MaxRequestsPerSecond)
            {
                _windows[deviceId] = window;
                return false;
            }

            _windows[deviceId] = (window.WindowStartMs, window.Count + 1);
            return true;
        }
    }

    public void Forget(string deviceId)
    {
        lock (_gate) _windows.Remove(deviceId);
    }
}
