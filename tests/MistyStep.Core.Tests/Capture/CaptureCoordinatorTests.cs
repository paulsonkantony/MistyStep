using MistyStep.Core.Capture;
using MistyStep.Core.Models;
using MistyStep.Core.Store;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Capture;

public class CaptureCoordinatorTests
{
    private sealed class FakeSource : IClipboardSource
    {
        public event Action<RawClip, ReadTrigger>? Captured;

        public void Raise(RawClip clip, ReadTrigger trigger = ReadTrigger.ClipboardChange) => Captured?.Invoke(clip, trigger);
    }

    private sealed class FakeEncoder : IImageEncoder
    {
        public Func<ImageCandidate, ImageEncodeResult> Result { get; set; } =
            _ => new ImageEncodeResult.Ok([1, 2, 3], "image/png", 10, 20);

        public ImageEncodeResult Encode(ImageCandidate candidate, CaptureLimits limits) => Result(candidate);
    }

    /// <summary>Runs each encode only when the test releases it, so completion order can be controlled.</summary>
    private sealed class ManualRunner
    {
        public List<(Func<ImageEncodeResult> Work, TaskCompletionSource<ImageEncodeResult> Done)> Pending { get; } = [];

        public Task<ImageEncodeResult> Run(Func<ImageEncodeResult> work)
        {
            var done = new TaskCompletionSource<ImageEncodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add((work, done));
            return done.Task;
        }

        public void Release(int index) => Pending[index].Done.SetResult(Pending[index].Work());
    }

    private readonly FakeClock _clock = new();
    private readonly FakeSource _source = new();
    private readonly FakeEncoder _encoder = new();
    private readonly ClipStore _store;
    private CaptureContext _context = new(false, CaptureMode.Automatic, false);
    private readonly List<CaptureOutcome> _outcomes = [];

    public CaptureCoordinatorTests() => _store = new ClipStore(_clock, 10);

    private CaptureCoordinator Create(ManualRunner? runner = null)
    {
        var coordinator = new CaptureCoordinator(_source, _store, _encoder, () => _context, () => new CaptureLimits(), _clock,
            runner is null ? work => Task.FromResult(work()) : runner.Run);
        coordinator.Completed += o => { lock (_outcomes) _outcomes.Add(o); };
        return coordinator;
    }

    private static FakeRawClip Image() => new FakeRawClip().Bytes(ClipboardFormats.Dib, TestImages.FakeDib());

    [Fact]
    public void Text_is_stored_immediately()
    {
        using var coordinator = Create();
        _source.Raise(new FakeRawClip().Text("hello"));

        Assert.Equal("hello", _store.GetCurrent().Item?.Text);
        var stored = Assert.IsType<CaptureOutcome.Stored>(Assert.Single(_outcomes));
        Assert.Equal(ClipType.Text, stored.Type);
        Assert.Equal(5, stored.Size);
        Assert.Equal("text", stored.Source);
    }

    [Fact]
    public void Image_is_encoded_and_stored()
    {
        using var coordinator = Create();
        _source.Raise(Image());

        var item = _store.GetCurrent().Item!;
        Assert.Equal(ClipType.Image, item.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, item.Bytes.ToArray());
        Assert.Equal((10, 20), (item.Width, item.Height));
        var stored = Assert.IsType<CaptureOutcome.Stored>(Assert.Single(_outcomes));
        Assert.Equal("dib", stored.Source);
        Assert.Equal(3, stored.Size);
    }

    [Fact]
    public void Ignored_copy_leaves_the_store_unchanged()
    {
        using var coordinator = Create();
        _source.Raise(new FakeRawClip().Text("keep"));
        _source.Raise(new FakeRawClip().Text("secret").Marker(ClipboardFormats.ExcludeFromMonitor));

        Assert.Equal("keep", _store.GetCurrent().Item?.Text);
        var ignored = Assert.IsType<CaptureOutcome.Ignored>(_outcomes[1]);
        Assert.Equal(IgnoreReason.Secret, ignored.Reason);
        Assert.False(ignored.ShowHint);
    }

    [Fact]
    public void Image_too_large_is_ignored_with_a_hint()
    {
        _encoder.Result = _ => ImageEncodeResult.TooLarge.Instance;
        using var coordinator = Create();
        _source.Raise(new FakeRawClip().Text("keep"));
        _source.Raise(Image());

        Assert.Equal("keep", _store.GetCurrent().Item?.Text);
        var ignored = Assert.IsType<CaptureOutcome.Ignored>(_outcomes[1]);
        Assert.Equal(IgnoreReason.ImageTooLarge, ignored.Reason);
        Assert.True(ignored.ShowHint);
    }

    [Fact]
    public void Unreadable_image_is_ignored()
    {
        _encoder.Result = _ => ImageEncodeResult.Unreadable.Instance;
        using var coordinator = Create();
        _source.Raise(Image());
        Assert.False(_store.GetCurrent().HasItem);
        Assert.Equal(IgnoreReason.NoUsableContent, Assert.IsType<CaptureOutcome.Ignored>(Assert.Single(_outcomes)).Reason);
    }

    [Fact]
    public async Task Slow_image_finishing_after_newer_text_is_discarded()
    {
        var runner = new ManualRunner();
        using var coordinator = Create(runner);

        _source.Raise(Image());                       // starts encoding
        _source.Raise(new FakeRawClip().Text("newer")); // stored at once
        runner.Release(0);
        await runner.Pending[0].Done.Task;
        await WaitForOutcomes(2);

        Assert.Equal("newer", _store.GetCurrent().Item?.Text);
        Assert.IsType<CaptureOutcome.Superseded>(_outcomes[1]);
    }

    [Fact]
    public async Task Of_two_images_only_the_newer_wins_whatever_order_they_finish()
    {
        var runner = new ManualRunner();
        _encoder.Result = c => new ImageEncodeResult.Ok(((ImageCandidate.Dib)c).Bytes[..1], "image/png", 1, 1);
        using var coordinator = Create(runner);

        _source.Raise(new FakeRawClip().Bytes(ClipboardFormats.Dib, [1, 1]));
        _source.Raise(new FakeRawClip().Bytes(ClipboardFormats.Dib, [2, 2]));
        runner.Release(1); // newer first
        runner.Release(0); // older last
        await WaitForOutcomes(2);

        Assert.Equal(new byte[] { 2 }, _store.GetCurrent().Item!.Bytes.ToArray());
        Assert.Single(_outcomes.OfType<CaptureOutcome.Stored>());
        Assert.Single(_outcomes.OfType<CaptureOutcome.Superseded>());
    }

    [Fact]
    public void Hotkey_trigger_captures_in_manual_mode_and_others_do_not()
    {
        _context = new CaptureContext(false, CaptureMode.Manual, false);
        using var coordinator = Create();

        _source.Raise(new FakeRawClip().Text("auto"), ReadTrigger.ClipboardChange);
        _source.Raise(new FakeRawClip().Text("watchdog"), ReadTrigger.Watchdog);
        Assert.False(_store.GetCurrent().HasItem);

        _source.Raise(new FakeRawClip().Text("hotkey"), ReadTrigger.Hotkey);
        Assert.Equal("hotkey", _store.GetCurrent().Item?.Text);
        Assert.Equal(ReadTrigger.Hotkey, _outcomes[^1].Trigger);
    }

    [Fact]
    public void Encoder_exception_is_reported_by_type_only()
    {
        _encoder.Result = _ => throw new InvalidOperationException("contains SECRET");
        using var coordinator = Create();
        _source.Raise(Image());

        var failed = Assert.IsType<CaptureOutcome.Failed>(Assert.Single(_outcomes));
        Assert.Equal(nameof(InvalidOperationException), failed.ErrorType);
        Assert.DoesNotContain("SECRET", failed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_unsubscribes()
    {
        var coordinator = Create();
        coordinator.Dispose();
        _source.Raise(new FakeRawClip().Text("late"));
        Assert.False(_store.GetCurrent().HasItem);
    }

    private async Task WaitForOutcomes(int count)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (_outcomes) if (_outcomes.Count >= count) return;
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"expected {count} outcomes");
    }
}
