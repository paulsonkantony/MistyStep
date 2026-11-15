using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using MistyStep.Core.Config;
using MistyStep.Core.Pairing;
using MistyStep.Core.Security;
using MistyStep.Core.Time;

namespace MistyStep.Core.Auth;

/// <summary>A paired device as shown in the UI. Never carries the token or its hash.</summary>
public sealed record Device(string Id, string Name, string BaseUrl, DateTime CreatedUtc, DateTime? LastSeenUtc, long FetchCount);

/// <summary>A pairing code shown in the wizard's QR.</summary>
public sealed record PairingCode(string Code, string BaseUrl, long ExpiresAtMs)
{
    public override string ToString() => $"PairingCode {{ BaseUrl = {BaseUrl}, ExpiresAtMs = {ExpiresAtMs} }}";
}

public abstract record PairingStatus
{
    private PairingStatus() { }

    /// <summary>Not scanned yet.</summary>
    public sealed record Waiting(long ExpiresAtMs) : PairingStatus;

    /// <summary>A device opened the pairing page. <see cref="Device.LastSeenUtc"/> is set once it calls the API.</summary>
    public sealed record Redeemed(Device Device, long ExpiresAtMs) : PairingStatus;

    /// <summary>Expired, cancelled, or never existed.</summary>
    public sealed record Gone : PairingStatus
    {
        public static readonly Gone Instance = new();
    }
}

public enum RedeemFailure
{
    /// <summary>Unknown, expired or cancelled code.</summary>
    UnknownOrExpired,

    /// <summary>The code was already redeemed by a different address.</summary>
    BoundToOtherAddress,
}

public abstract record RedeemResult
{
    private RedeemResult() { }

    /// <summary>The pairing page can show <see cref="SetupCode"/>. Repeats for reloads from the same address.</summary>
    public sealed record Redeemed(Device Device, string SetupCode) : RedeemResult
    {
        public override string ToString() => $"Redeemed {{ Device = {Device.Id} }}";
    }

    /// <summary>Answer 410.</summary>
    public sealed record Gone(RedeemFailure Failure) : RedeemResult;
}

public enum RegistryChange
{
    /// <summary>Devices added, removed or renamed: save now.</summary>
    Structural,

    /// <summary>Last-seen or fetch count changed: save at most once a minute.</summary>
    Activity,
}

public interface IDeviceRegistry
{
    event Action<RegistryChange>? Changed;

    IReadOnlyList<Device> Devices { get; }

    PairingCode CreatePairingCode(string baseUrl, string pcName);
    RedeemResult Redeem(string? code, IPAddress remote, string? userAgent);
    void CancelPairing(string code);

    /// <summary>For the pairing wizard: whether a code is still waiting, redeemed (and by which device), or gone.</summary>
    PairingStatus GetPairingStatus(string code);

    Device? Authenticate(string? token);
    void RecordSeen(string deviceId);
    void RecordDelivery(string deviceId, string itemId);
    bool HasReceived(string deviceId, string itemId);

    bool Rename(string deviceId, string name);
    bool Revoke(string deviceId);

    IReadOnlyList<DeviceRecord> ToRecords();
    void Load(IEnumerable<DeviceRecord> records);
}

/// <summary>
/// Paired devices, outstanding pairing codes and per-device delivery state, guarded by one lock.
/// Only SHA-256 hashes of tokens are kept, except a token's plaintext which lives with its pairing
/// code until the code expires (so the pairing page can be reloaded) and is then dropped.
/// </summary>
public sealed class DeviceRegistry(IClock clock) : IDeviceRegistry
{
    public const long PairingCodeLifetimeMs = 10 * 60_000;
    public const int MaxFailedPairingAttempts = 6;
    public const int MaxNameLength = 64;
    private const int PairingCodeBytes = 16;

    private sealed class Entry
    {
        public required string Id;
        public required string Name;
        public required byte[] TokenHash;
        public required string BaseUrl;
        public required DateTime CreatedUtc;
        public DateTime? LastSeenUtc;
        public long FetchCount;
        public string? LastDeliveredId; // memory only

        public Device ToDevice() => new(Id, Name, BaseUrl, CreatedUtc, LastSeenUtc, FetchCount);
    }

    private sealed class Pending
    {
        public required string Code;
        public required string BaseUrl;
        public required string PcName;
        public required long ExpiresAtMs;
        public IPAddress? BoundAddress;
        public string? DeviceId;
        public string? SetupCode; // holds the plaintext token until expiry
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _byHashHex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private int _failedPairingAttempts;

    public event Action<RegistryChange>? Changed;

    public IReadOnlyList<Device> Devices
    {
        get
        {
            lock (_gate) return _byId.Values.OrderBy(e => e.CreatedUtc).Select(e => e.ToDevice()).ToList();
        }
    }

    /// <summary>Number of outstanding (unexpired) pairing codes.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                PruneExpired();
                return _pending.Count;
            }
        }
    }

    public PairingCode CreatePairingCode(string baseUrl, string pcName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(pcName);
        lock (_gate)
        {
            PruneExpired();
            if (_pending.Count == 0) _failedPairingAttempts = 0; // a fresh pairing session

            var code = Secrets.NewHexId(PairingCodeBytes);
            var pending = new Pending
            {
                Code = code,
                BaseUrl = baseUrl,
                PcName = pcName,
                ExpiresAtMs = clock.MonotonicMs + PairingCodeLifetimeMs,
            };
            _pending[code] = pending;
            return new PairingCode(code, baseUrl, pending.ExpiresAtMs);
        }
    }

    public RedeemResult Redeem(string? code, IPAddress remote, string? userAgent)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();

        RedeemResult result;
        var structural = false;
        lock (_gate)
        {
            PruneExpired();
            if (code is null || !_pending.TryGetValue(code, out var pending))
            {
                result = Fail(RedeemFailure.UnknownOrExpired);
            }
            else if (pending.BoundAddress is null)
            {
                var token = Secrets.NewToken();
                var entry = new Entry
                {
                    Id = NewDeviceId(),
                    Name = DeviceNamer.FromUserAgent(userAgent, clock.UtcNow.LocalDateTime),
                    TokenHash = Secrets.Sha256(token),
                    BaseUrl = pending.BaseUrl,
                    CreatedUtc = clock.UtcNow.UtcDateTime,
                };
                Add(entry);

                pending.BoundAddress = remote;
                pending.DeviceId = entry.Id;
                pending.SetupCode = SetupCode.Encode(new SetupInfo(pending.BaseUrl, token, pending.PcName));
                result = new RedeemResult.Redeemed(entry.ToDevice(), pending.SetupCode);
                structural = true;
            }
            else if (pending.BoundAddress.Equals(remote)
                     && pending.DeviceId is not null
                     && pending.SetupCode is not null
                     && _byId.TryGetValue(pending.DeviceId, out var existing))
            {
                result = new RedeemResult.Redeemed(existing.ToDevice(), pending.SetupCode);
            }
            else
            {
                result = Fail(RedeemFailure.BoundToOtherAddress);
            }
        }

        if (structural) Changed?.Invoke(RegistryChange.Structural);
        return result;
    }

    public PairingStatus GetPairingStatus(string code)
    {
        lock (_gate)
        {
            PruneExpired();
            if (!_pending.TryGetValue(code, out var pending)) return PairingStatus.Gone.Instance;
            if (pending.DeviceId is not null && _byId.TryGetValue(pending.DeviceId, out var entry))
                return new PairingStatus.Redeemed(entry.ToDevice(), pending.ExpiresAtMs);
            return pending.DeviceId is null ? new PairingStatus.Waiting(pending.ExpiresAtMs) : PairingStatus.Gone.Instance;
        }
    }

    /// <summary>Cancels a pairing code. If a device already redeemed it, that device is revoked.</summary>
    public void CancelPairing(string code)
    {
        var structural = false;
        lock (_gate)
        {
            if (!_pending.Remove(code, out var pending)) return;
            pending.SetupCode = null;
            if (pending.DeviceId is not null) structural = RemoveEntry(pending.DeviceId);
        }

        if (structural) Changed?.Invoke(RegistryChange.Structural);
    }

    public Device? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Secrets.Sha256(token);
        var hex = Convert.ToHexStringLower(hash);
        lock (_gate)
        {
            if (!_byHashHex.TryGetValue(hex, out var entry)) return null;
            return CryptographicOperations.FixedTimeEquals(entry.TokenHash, hash) ? entry.ToDevice() : null;
        }
    }

    public void RecordSeen(string deviceId)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(deviceId, out var entry)) return;
            entry.LastSeenUtc = clock.UtcNow.UtcDateTime;
        }

        Changed?.Invoke(RegistryChange.Activity);
    }

    public void RecordDelivery(string deviceId, string itemId)
    {
        string name;
        lock (_gate)
        {
            if (!_byId.TryGetValue(deviceId, out var entry)) return;
            entry.LastDeliveredId = itemId;
            entry.FetchCount++;
            name = entry.Name;
        }

        Changed?.Invoke(RegistryChange.Activity);
        Delivered?.Invoke(name, itemId);
    }

    /// <summary>A device received an item: (device name, item id). Drives the tray pulse and the optional toast.</summary>
    public event Action<string, string>? Delivered;

    public bool HasReceived(string deviceId, string itemId)
    {
        lock (_gate) return _byId.TryGetValue(deviceId, out var entry) && entry.LastDeliveredId == itemId;
    }

    public bool Rename(string deviceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (name.Length == 0 || name.Length > MaxNameLength) return false;
        lock (_gate)
        {
            if (!_byId.TryGetValue(deviceId, out var entry)) return false;
            entry.Name = name;
        }

        Changed?.Invoke(RegistryChange.Structural);
        return true;
    }

    public bool Revoke(string deviceId)
    {
        bool removed;
        lock (_gate)
        {
            removed = RemoveEntry(deviceId);
            foreach (var pending in _pending.Values)
            {
                if (pending.DeviceId == deviceId) pending.SetupCode = null;
            }
        }

        if (removed) Changed?.Invoke(RegistryChange.Structural);
        return removed;
    }

    public IReadOnlyList<DeviceRecord> ToRecords()
    {
        lock (_gate)
        {
            return _byId.Values
                .OrderBy(e => e.CreatedUtc)
                .Select(e => new DeviceRecord(e.Id, e.Name, Convert.ToHexStringLower(e.TokenHash), e.BaseUrl,
                    e.CreatedUtc, e.LastSeenUtc, e.FetchCount))
                .ToList();
        }
    }

    /// <summary>Replaces all devices with <paramref name="records"/>. Invalid records are skipped.</summary>
    public void Load(IEnumerable<DeviceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            _byId.Clear();
            _byHashHex.Clear();
            foreach (var record in records)
            {
                if (record is null || !record.IsValid() || _byId.ContainsKey(record.Id)) continue;
                var hash = Convert.FromHexString(record.TokenHash);
                if (_byHashHex.ContainsKey(Convert.ToHexStringLower(hash))) continue;
                Add(new Entry
                {
                    Id = record.Id,
                    Name = record.Name,
                    TokenHash = hash,
                    BaseUrl = record.BaseUrl,
                    CreatedUtc = DateTime.SpecifyKind(record.CreatedUtc, DateTimeKind.Utc),
                    LastSeenUtc = record.LastSeenUtc is { } seen ? DateTime.SpecifyKind(seen, DateTimeKind.Utc) : null,
                    FetchCount = record.FetchCount,
                });
            }
        }
    }

    // --- helpers; all called with _gate held ---

    private RedeemResult.Gone Fail(RedeemFailure failure)
    {
        if (++_failedPairingAttempts >= MaxFailedPairingAttempts)
        {
            foreach (var pending in _pending.Values) pending.SetupCode = null;
            _pending.Clear();
            _failedPairingAttempts = 0;
        }

        return new RedeemResult.Gone(failure);
    }

    private void PruneExpired()
    {
        var now = clock.MonotonicMs;
        List<string>? expired = null;
        foreach (var (code, pending) in _pending)
        {
            if (now >= pending.ExpiresAtMs) (expired ??= []).Add(code);
        }

        if (expired is null) return;
        foreach (var code in expired)
        {
            _pending[code].SetupCode = null;
            _pending.Remove(code);
        }
    }

    private void Add(Entry entry)
    {
        _byId[entry.Id] = entry;
        _byHashHex[Convert.ToHexStringLower(entry.TokenHash)] = entry;
    }

    private bool RemoveEntry(string deviceId)
    {
        if (!_byId.Remove(deviceId, out var entry)) return false;
        _byHashHex.Remove(Convert.ToHexStringLower(entry.TokenHash));
        return true;
    }

    private string NewDeviceId()
    {
        string id;
        do id = "d_" + Secrets.NewHexId(3);
        while (_byId.ContainsKey(id));
        return id;
    }
}

/// <summary>Names a new device from its user agent and the date, e.g. "iPad (Nov 14)". The user can rename it.</summary>
public static class DeviceNamer
{
    public static string FromUserAgent(string? userAgent, DateTime localNow)
    {
        var kind = userAgent switch
        {
            null => "Device",
            _ when userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) => "iPhone",
            _ when userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) => "iPad",
            // Safari on iPadOS requests desktop sites and reports itself as a Mac.
            _ when userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) => "iPad",
            _ => "Device",
        };
        return $"{kind} ({localNow.ToString("MMM d", CultureInfo.InvariantCulture)})";
    }
}
