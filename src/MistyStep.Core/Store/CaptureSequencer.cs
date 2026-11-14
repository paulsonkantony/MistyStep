using MistyStep.Core.Models;

namespace MistyStep.Core.Store;

/// <summary>
/// Orders accepted captures so an older image encode that finishes late never replaces a newer item.
/// Call <see cref="Begin"/> only for reads the decider accepted: an ignored copy must not change the store.
/// </summary>
public sealed class CaptureSequencer(IClipStore store)
{
    private readonly Lock _gate = new();
    private long _latest;

    /// <summary>Starts an accepted capture and returns its generation.</summary>
    public long Begin()
    {
        lock (_gate) return ++_latest;
    }

    /// <summary>Stores <paramref name="item"/> only if no newer capture has begun since <paramref name="generation"/>.</summary>
    public bool TryCommit(long generation, ClipItem item)
    {
        lock (_gate)
        {
            if (generation != _latest) return false;
            store.Set(item);
            return true;
        }
    }
}
