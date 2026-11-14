using MistyStep.Core.Models;
using MistyStep.Core.Store;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Store;

public class ClipStoreTests
{
    private readonly FakeClock _clock = new();

    private ClipItem NewText(string text = "hello") => ClipItem.CreateText(text, _clock.MonotonicMs);

    [Fact]
    public void Starts_empty()
    {
        var snapshot = new ClipStore(_clock, 10).GetCurrent();
        Assert.False(snapshot.HasItem);
        Assert.Equal(NoneReason.Empty, snapshot.Reason);
    }

    [Fact]
    public void Set_then_get_returns_item()
    {
        var store = new ClipStore(_clock, 10);
        var item = NewText();
        store.Set(item);
        Assert.Same(item, store.GetCurrent().Item);
    }

    [Fact]
    public void Set_replaces_previous_item()
    {
        var store = new ClipStore(_clock, 10);
        store.Set(NewText("a"));
        var second = NewText("b");
        store.Set(second);
        Assert.Same(second, store.GetCurrent().Item);
    }

    [Fact]
    public void Item_is_present_just_before_expiry()
    {
        var store = new ClipStore(_clock, 10);
        store.Set(NewText());
        _clock.Advance(10 * 60_000 - 1);
        Assert.True(store.GetCurrent().HasItem);
    }

    [Fact]
    public void Item_expires_exactly_at_limit()
    {
        var store = new ClipStore(_clock, 10);
        store.Set(NewText());
        _clock.Advance(10 * 60_000);
        var snapshot = store.GetCurrent();
        Assert.False(snapshot.HasItem);
        Assert.Equal(NoneReason.Expired, snapshot.Reason);
    }

    [Fact]
    public void Item_stays_expired_after_limit()
    {
        var store = new ClipStore(_clock, 10);
        store.Set(NewText());
        _clock.Advance(10 * 60_000 + 1);
        Assert.Equal(NoneReason.Expired, store.GetCurrent().Reason);
        _clock.Advance(60_000);
        Assert.Equal(NoneReason.Expired, store.GetCurrent().Reason);
    }

    [Fact]
    public void Zero_minutes_never_expires()
    {
        var store = new ClipStore(_clock, 0);
        store.Set(NewText());
        _clock.Advance(365L * 24 * 60 * 60_000);
        Assert.True(store.GetCurrent().HasItem);
    }

    [Fact]
    public void Expiry_change_applies_to_current_item()
    {
        var store = new ClipStore(_clock, 0);
        store.Set(NewText());
        _clock.Advance(3 * 60_000);
        store.ExpiryMinutes = 2;
        Assert.Equal(NoneReason.Expired, store.GetCurrent().Reason);
    }

    [Fact]
    public void New_item_after_expiry_is_served()
    {
        var store = new ClipStore(_clock, 2);
        store.Set(NewText("old"));
        _clock.Advance(2 * 60_000);
        Assert.False(store.GetCurrent().HasItem);
        var fresh = NewText("new");
        store.Set(fresh);
        Assert.Same(fresh, store.GetCurrent().Item);
    }

    [Fact]
    public void Clear_empties_the_store()
    {
        var store = new ClipStore(_clock, 10);
        store.Set(NewText());
        store.Clear();
        var snapshot = store.GetCurrent();
        Assert.False(snapshot.HasItem);
        Assert.Equal(NoneReason.Empty, snapshot.Reason);
    }

    [Fact]
    public void Negative_expiry_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClipStore(_clock, -1));
}

public class CaptureSequencerTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void Latest_capture_commits()
    {
        var store = new ClipStore(_clock, 10);
        var sequencer = new CaptureSequencer(store);
        var generation = sequencer.Begin();
        var item = ClipItem.CreateImage(new byte[] { 1 }, "image/png", 1, 1, _clock.MonotonicMs);
        Assert.True(sequencer.TryCommit(generation, item));
        Assert.Same(item, store.GetCurrent().Item);
    }

    [Fact]
    public void Older_encode_finishing_after_newer_capture_does_not_win()
    {
        var store = new ClipStore(_clock, 10);
        var sequencer = new CaptureSequencer(store);

        var slowImage = sequencer.Begin();
        var fastText = sequencer.Begin();

        var text = ClipItem.CreateText("newer", _clock.MonotonicMs);
        Assert.True(sequencer.TryCommit(fastText, text));

        var image = ClipItem.CreateImage(new byte[] { 1 }, "image/png", 1, 1, _clock.MonotonicMs);
        Assert.False(sequencer.TryCommit(slowImage, image));

        Assert.Same(text, store.GetCurrent().Item);
    }

    [Fact]
    public void Older_encode_cannot_commit_even_before_newer_finishes()
    {
        var store = new ClipStore(_clock, 10);
        var sequencer = new CaptureSequencer(store);
        var older = sequencer.Begin();
        _ = sequencer.Begin();
        Assert.False(sequencer.TryCommit(older, ClipItem.CreateText("old", 0)));
        Assert.False(store.GetCurrent().HasItem);
    }
}
