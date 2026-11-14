using MistyStep.Core.Models;
using MistyStep.Core.Time;

namespace MistyStep.Core.Store;

/// <summary>The current item, or why there is none (<see cref="NoneReason.Empty"/> or <see cref="NoneReason.Expired"/>).</summary>
public readonly record struct ClipSnapshot(ClipItem? Item, NoneReason Reason)
{
    public bool HasItem => Item is not null;
}

public interface IClipStore
{
    ClipSnapshot GetCurrent();
    void Set(ClipItem item);
    void Clear();

    /// <summary>Expiry in minutes; 0 means the item lasts until replaced.</summary>
    int ExpiryMinutes { get; set; }
}

/// <summary>
/// Holds exactly one immutable item. The state reference is swapped atomically, so readers on
/// Kestrel threads never see a half-updated item. An expired item's reference is dropped on read.
/// </summary>
public sealed class ClipStore : IClipStore
{
    private sealed record State(ClipItem? Item, NoneReason ReasonWhenNone);

    private static readonly State EmptyState = new(null, NoneReason.Empty);
    private static readonly State ExpiredState = new(null, NoneReason.Expired);

    private readonly IClock _clock;
    private State _state = EmptyState;
    private int _expiryMinutes;

    public ClipStore(IClock clock, int expiryMinutes)
    {
        _clock = clock;
        ExpiryMinutes = expiryMinutes;
    }

    public int ExpiryMinutes
    {
        get => Volatile.Read(ref _expiryMinutes);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref _expiryMinutes, value);
        }
    }

    public ClipSnapshot GetCurrent()
    {
        var state = Volatile.Read(ref _state);
        if (state.Item is null) return new ClipSnapshot(null, state.ReasonWhenNone);

        var minutes = ExpiryMinutes;
        if (minutes > 0 && _clock.MonotonicMs - state.Item.CapturedAt >= minutes * 60_000L)
        {
            // Release the expired item, unless a newer one was swapped in meanwhile.
            Interlocked.CompareExchange(ref _state, ExpiredState, state);
            return new ClipSnapshot(null, NoneReason.Expired);
        }

        return new ClipSnapshot(state.Item, NoneReason.Empty);
    }

    public void Set(ClipItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Interlocked.Exchange(ref _state, new State(item, NoneReason.Empty));
    }

    public void Clear() => Interlocked.Exchange(ref _state, EmptyState);
}
