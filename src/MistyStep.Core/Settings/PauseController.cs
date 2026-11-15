using MistyStep.Core.Time;

namespace MistyStep.Core.Settings;

/// <summary>Why sharing is paused right now.</summary>
public enum PauseReason { None, User, ScreenLocked }

public readonly record struct PauseStatus(PauseReason Reason, long? ResumesAtMs)
{
    public bool IsPaused => Reason != PauseReason.None;
}

/// <summary>
/// "Pause sharing" with automatic resume (5 minutes, 1 hour, or until resumed), plus the
/// <c>serveWhileLocked</c> rule: when it is off, a locked screen pauses sharing too.
/// Times use the monotonic clock, so wall-clock changes do not end a pause early or late.
/// </summary>
public sealed class PauseController(IClock clock)
{
    private readonly object _gate = new();
    private bool _userPaused;
    private long? _resumesAtMs;

    public bool ScreenLocked { get; set; }
    public bool ServeWhileLocked { get; set; } = true;

    /// <summary>Pauses for <paramref name="duration"/>, or until <see cref="Resume"/> when null.</summary>
    public void Pause(TimeSpan? duration)
    {
        if (duration is { } d) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(d, TimeSpan.Zero);
        lock (_gate)
        {
            _userPaused = true;
            _resumesAtMs = duration is { } span ? clock.MonotonicMs + (long)span.TotalMilliseconds : null;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _userPaused = false;
            _resumesAtMs = null;
        }
    }

    public PauseStatus Current
    {
        get
        {
            lock (_gate)
            {
                if (_userPaused && _resumesAtMs is { } at && clock.MonotonicMs >= at)
                {
                    _userPaused = false;
                    _resumesAtMs = null;
                }

                if (_userPaused) return new PauseStatus(PauseReason.User, _resumesAtMs);
                if (ScreenLocked && !ServeWhileLocked) return new PauseStatus(PauseReason.ScreenLocked, null);
                return new PauseStatus(PauseReason.None, null);
            }
        }
    }

    /// <summary>True when the user chose "Until I resume".</summary>
    public bool IsIndefinite
    {
        get
        {
            var current = Current;
            return current.Reason == PauseReason.User && current.ResumesAtMs is null;
        }
    }
}
