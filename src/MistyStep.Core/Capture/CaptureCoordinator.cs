using MistyStep.Core.Models;
using MistyStep.Core.Store;
using MistyStep.Core.Time;

namespace MistyStep.Core.Capture;

/// <summary>Turns an image candidate into servable bytes. Runs on the thread pool; must not touch the clipboard.</summary>
public interface IImageEncoder
{
    ImageEncodeResult Encode(ImageCandidate candidate, CaptureLimits limits);
}

public abstract record ImageEncodeResult
{
    private ImageEncodeResult() { }

    public sealed record Ok(byte[] Bytes, string Mime, int Width, int Height) : ImageEncodeResult;

    /// <summary>Still over the image limit after downscaling and (when opaque) JPEG.</summary>
    public sealed record TooLarge : ImageEncodeResult
    {
        public static readonly TooLarge Instance = new();
    }

    /// <summary>The bytes could not be decoded as an image.</summary>
    public sealed record Unreadable : ImageEncodeResult
    {
        public static readonly Unreadable Instance = new();
    }
}

/// <summary>What happened to one clipboard read. Carries type and size only, never content.</summary>
public abstract record CaptureOutcome(ReadTrigger Trigger)
{
    /// <summary>The item is now the shared item. <see cref="Source"/> is text, png, dib or file.</summary>
    public sealed record Stored(ReadTrigger Trigger, ClipType Type, string Mime, long Size, int Width, int Height, string Source)
        : CaptureOutcome(Trigger);

    public sealed record Ignored(ReadTrigger Trigger, IgnoreReason Reason) : CaptureOutcome(Trigger)
    {
        /// <summary>Only size skips get a tray hint; secrets and excluded apps stay silent.</summary>
        public bool ShowHint => Reason is IgnoreReason.TextTooLarge or IgnoreReason.ImageTooLarge;
    }

    /// <summary>A newer copy arrived while this image was encoding, so it was discarded.</summary>
    public sealed record Superseded(ReadTrigger Trigger) : CaptureOutcome(Trigger);

    /// <summary>Reading or encoding threw. Only the exception type is kept.</summary>
    public sealed record Failed(ReadTrigger Trigger, string ErrorType) : CaptureOutcome(Trigger);
}

/// <summary>
/// The copy-to-store pipeline: decide on the UI thread while the clipboard is open, store text at once,
/// encode images on the thread pool and store them only if no newer capture began meanwhile.
/// <see cref="Completed"/> fires on the UI thread for text and ignored copies, and on a pool thread for images.
/// </summary>
public sealed class CaptureCoordinator : IDisposable
{
    private readonly IClipboardSource _source;
    private readonly ClipDecider _decider;
    private readonly CaptureSequencer _sequencer;
    private readonly IImageEncoder _encoder;
    private readonly Func<CaptureContext> _context;
    private readonly Func<CaptureLimits> _limits;
    private readonly IClock _clock;
    private readonly Func<Func<ImageEncodeResult>, Task<ImageEncodeResult>> _runEncode;

    public CaptureCoordinator(
        IClipboardSource source,
        IClipStore store,
        IImageEncoder encoder,
        Func<CaptureContext> context,
        Func<CaptureLimits> limits,
        IClock clock,
        Func<Func<ImageEncodeResult>, Task<ImageEncodeResult>>? runEncode = null)
    {
        _source = source;
        _decider = new ClipDecider(clock);
        _sequencer = new CaptureSequencer(store);
        _encoder = encoder;
        _context = context;
        _limits = limits;
        _clock = clock;
        _runEncode = runEncode ?? (work => Task.Run(work));
        _source.Captured += OnCaptured;
    }

    public event Action<CaptureOutcome>? Completed;

    public void Dispose() => _source.Captured -= OnCaptured;

    private void OnCaptured(RawClip clip, ReadTrigger trigger)
    {
        CaptureResult result;
        CaptureLimits limits;
        try
        {
            var context = _context() with { HotkeyTriggered = trigger == ReadTrigger.Hotkey };
            limits = _limits();
            result = _decider.Decide(clip, context, limits);
        }
        catch (Exception ex)
        {
            Completed?.Invoke(new CaptureOutcome.Failed(trigger, ex.GetType().Name));
            return;
        }

        switch (result)
        {
            case CaptureResult.Ignored ignored:
                Completed?.Invoke(new CaptureOutcome.Ignored(trigger, ignored.Reason));
                break;

            case CaptureResult.Text text:
                var generation = _sequencer.Begin();
                var stored = _sequencer.TryCommit(generation, text.Item);
                Completed?.Invoke(stored
                    ? new CaptureOutcome.Stored(trigger, ClipType.Text, text.Item.Mime,
                        System.Text.Encoding.UTF8.GetByteCount(text.Item.Text!), 0, 0, "text")
                    : new CaptureOutcome.Superseded(trigger));
                break;

            case CaptureResult.Image image:
                _ = EncodeAndCommitAsync(image.Candidate, limits, trigger, _sequencer.Begin(), _clock.MonotonicMs);
                break;
        }
    }

    private async Task EncodeAndCommitAsync(ImageCandidate candidate, CaptureLimits limits, ReadTrigger trigger, long generation, long capturedAt)
    {
        CaptureOutcome outcome;
        try
        {
            var result = await _runEncode(() => _encoder.Encode(candidate, limits)).ConfigureAwait(false);
            outcome = result switch
            {
                ImageEncodeResult.Ok ok => _sequencer.TryCommit(generation, ClipItem.CreateImage(ok.Bytes, ok.Mime, ok.Width, ok.Height, capturedAt))
                    ? new CaptureOutcome.Stored(trigger, ClipType.Image, ok.Mime, ok.Bytes.Length, ok.Width, ok.Height, SourceName(candidate))
                    : new CaptureOutcome.Superseded(trigger),
                ImageEncodeResult.TooLarge => new CaptureOutcome.Ignored(trigger, IgnoreReason.ImageTooLarge),
                _ => new CaptureOutcome.Ignored(trigger, IgnoreReason.NoUsableContent),
            };
        }
        catch (Exception ex)
        {
            outcome = new CaptureOutcome.Failed(trigger, ex.GetType().Name);
        }

        Completed?.Invoke(outcome);
    }

    private static string SourceName(ImageCandidate candidate) => candidate switch
    {
        ImageCandidate.PngBytes => "png",
        ImageCandidate.Dib => "dib",
        ImageCandidate.File => "file",
        _ => "image",
    };
}
