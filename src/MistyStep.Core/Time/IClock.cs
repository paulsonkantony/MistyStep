namespace MistyStep.Core.Time;

/// <summary>
/// Time source. <see cref="MonotonicMs"/> drives expiry, lockouts and rate limits so that
/// wall-clock changes have no effect; <see cref="UtcNow"/> is only for human-facing timestamps.
/// </summary>
public interface IClock
{
    /// <summary>Monotonic milliseconds (e.g. <see cref="Environment.TickCount64"/>).</summary>
    long MonotonicMs { get; }

    /// <summary>Wall-clock UTC time, used for device created/last-seen stamps.</summary>
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    private SystemClock() { }

    public long MonotonicMs => Environment.TickCount64;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
