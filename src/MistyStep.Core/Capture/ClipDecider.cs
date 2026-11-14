using System.Text;
using MistyStep.Core.Models;
using MistyStep.Core.Time;

namespace MistyStep.Core.Capture;

/// <summary>
/// The capture decision order from the spec. Each read yields at most one result: text if the
/// clipboard has any, otherwise an image source, otherwise ignored.
/// </summary>
public sealed class ClipDecider(IClock clock)
{
    private static readonly Dictionary<string, string> FileMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public CaptureResult Decide(RawClip clip, CaptureContext context, CaptureLimits limits)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(limits);

        // 1. Paused, or Manual mode without the hotkey.
        if (context.Paused) return new CaptureResult.Ignored(IgnoreReason.Paused);
        if (context.Mode == CaptureMode.Manual && !context.HotkeyTriggered)
            return new CaptureResult.Ignored(IgnoreReason.ManualMode);

        var formats = clip.Formats;

        // 2. Secrecy markers.
        if (limits.HonorSecrecyMarkers && HasSecrecyMarker(clip, formats))
            return new CaptureResult.Ignored(IgnoreReason.Secret);

        // 3. Excluded owner process. A null owner is not excluded.
        if (IsExcludedOwner(clip.OwnerProcessName, limits.ExcludedApps))
            return new CaptureResult.Ignored(IgnoreReason.ExcludedApp);

        // 4. Text.
        if (formats.Contains(ClipboardFormats.UnicodeText))
        {
            var raw = clip.GetUnicodeText();
            if (raw is not null)
            {
                var text = TextNormalizer.Normalize(raw);
                if (!TextNormalizer.IsBlank(text))
                {
                    if (Encoding.UTF8.GetByteCount(text) > limits.MaxTextBytes)
                        return new CaptureResult.Ignored(IgnoreReason.TextTooLarge);
                    return new CaptureResult.Text(ClipItem.CreateText(text, clock.MonotonicMs));
                }
            }
        }

        // 5. Image: PNG, then DIBV5, then DIB, then a single dropped image file.
        if (formats.Contains(ClipboardFormats.Png))
        {
            var png = clip.GetBytes(ClipboardFormats.Png);
            if (png is not null && PngInfo.TryRead(png, out var width, out var height))
                return new CaptureResult.Image(new ImageCandidate.PngBytes(png, width, height));
        }

        foreach (var (format, isV5) in new[] { (ClipboardFormats.DibV5, true), (ClipboardFormats.Dib, false) })
        {
            if (!formats.Contains(format)) continue;
            var dib = clip.GetBytes(format);
            if (dib is { Length: > 0 }) return new CaptureResult.Image(new ImageCandidate.Dib(dib, isV5));
        }

        if (formats.Contains(ClipboardFormats.HDrop))
        {
            var files = clip.GetFileDrop();
            if (files is { Count: 1 }) return DecideFile(clip, files[0], limits);
        }

        // 6. Anything else.
        return new CaptureResult.Ignored(IgnoreReason.NoUsableContent);
    }

    private static CaptureResult DecideFile(RawClip clip, string path, CaptureLimits limits)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase))
        {
            // Converted (and downscaled if needed) to PNG; the size limit applies to the result.
            return new CaptureResult.Image(new ImageCandidate.File(path, "image/png", ConvertToPng: true));
        }

        if (!FileMimes.TryGetValue(extension, out var mime))
            return new CaptureResult.Ignored(IgnoreReason.NoUsableContent);

        var length = clip.GetFileLength(path);
        if (length is null) return new CaptureResult.Ignored(IgnoreReason.NoUsableContent);
        if (length.Value > limits.MaxImageBytes) return new CaptureResult.Ignored(IgnoreReason.ImageTooLarge);

        return new CaptureResult.Image(new ImageCandidate.File(path, mime, ConvertToPng: false));
    }

    private static bool HasSecrecyMarker(RawClip clip, IReadOnlySet<string> formats)
    {
        if (formats.Contains(ClipboardFormats.ExcludeFromMonitor) || formats.Contains(ClipboardFormats.ViewerIgnore))
            return true;

        foreach (var format in (ReadOnlySpan<string>)[ClipboardFormats.CanIncludeInHistory, ClipboardFormats.CanUploadToCloud])
        {
            if (!formats.Contains(format)) continue;
            // A present marker whose value cannot be read is treated as secret: safe default.
            var value = clip.GetDword(format);
            if (value is null or 0) return true;
        }

        return false;
    }

    private static bool IsExcludedOwner(string? owner, IReadOnlyCollection<string> excluded)
    {
        if (string.IsNullOrEmpty(owner)) return false;
        foreach (var app in excluded)
        {
            if (string.Equals(app, owner, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
