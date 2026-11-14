using MistyStep.Core.Models;

namespace MistyStep.Core.Capture;

/// <summary>State that varies per read.</summary>
public sealed record CaptureContext(bool Paused, CaptureMode Mode, bool HotkeyTriggered);

/// <summary>Settings that shape capture. Defaults match the settings table.</summary>
public sealed record CaptureLimits
{
    public int MaxTextBytes { get; init; } = 1_048_576;
    public long MaxImageBytes { get; init; } = 26_214_400;
    public int MaxImageEdge { get; init; } = 4096;
    public bool HonorSecrecyMarkers { get; init; } = true;
    public IReadOnlyCollection<string> ExcludedApps { get; init; } = [];
}

public enum IgnoreReason
{
    Paused,
    ManualMode,
    Secret,
    ExcludedApp,
    TextTooLarge,
    ImageTooLarge,
    NoUsableContent,
}

/// <summary>Where an image comes from. Conversion and downscaling happen in the Windows layer.</summary>
public abstract record ImageCandidate
{
    private ImageCandidate() { }

    /// <summary>A valid PNG from the registered PNG format, used unchanged if it fits.</summary>
    public sealed record PngBytes(byte[] Bytes, int Width, int Height) : ImageCandidate;

    /// <summary>A device-independent bitmap that must be converted to PNG.</summary>
    public sealed record Dib(byte[] Bytes, bool IsV5) : ImageCandidate;

    /// <summary>A single dropped image file. <see cref="ConvertToPng"/> is true for .bmp.</summary>
    public sealed record File(string Path, string Mime, bool ConvertToPng) : ImageCandidate;
}

public abstract record CaptureResult
{
    private CaptureResult() { }

    public sealed record Ignored(IgnoreReason Reason) : CaptureResult
    {
        /// <summary>Only size skips get a tray hint; secrets and excluded apps stay silent.</summary>
        public bool ShowHint => Reason is IgnoreReason.TextTooLarge or IgnoreReason.ImageTooLarge;
    }

    public sealed record Text(ClipItem Item) : CaptureResult;

    public sealed record Image(ImageCandidate Candidate) : CaptureResult;
}
