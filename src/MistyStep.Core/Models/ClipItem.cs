using System.Text.Json.Serialization;
using MistyStep.Core.Capture;
using MistyStep.Core.Security;

namespace MistyStep.Core.Models;

public enum ClipType { Text, Image }

public enum CaptureMode
{
    [JsonStringEnumMemberName("automatic")] Automatic,
    [JsonStringEnumMemberName("manual")] Manual,
}

/// <summary>Why <c>/v1/meta</c> reports no item.</summary>
public enum NoneReason { Empty, Expired, Paused, Unchanged }

/// <summary>The one item the PC is currently sharing. Immutable.</summary>
public sealed record ClipItem(
    string Id,                  // 16 random bytes as hex; new for every capture
    ClipType Type,
    string? Text,               // set when Type == Text
    ReadOnlyMemory<byte> Bytes, // set when Type == Image
    string Mime,                // text/plain, image/png, image/jpeg, image/gif, image/webp
    int Width, int Height,      // 0 for text
    bool IsUrl,                 // Text is one absolute http or https URL
    long CapturedAt)            // IClock monotonic milliseconds
{
    public const int IdBytes = 16;

    public static ClipItem CreateText(string text, long capturedAt) =>
        new(Secrets.NewHexId(IdBytes), ClipType.Text, text, ReadOnlyMemory<byte>.Empty,
            "text/plain", 0, 0, UrlDetector.IsUrl(text), capturedAt);

    public static ClipItem CreateImage(ReadOnlyMemory<byte> bytes, string mime, int width, int height, long capturedAt) =>
        new(Secrets.NewHexId(IdBytes), ClipType.Image, null, bytes, mime, width, height, false, capturedAt);

    // Never print content: records generate a ToString that would include Text.
    public override string ToString() =>
        $"ClipItem {{ Type = {Type}, Mime = {Mime}, Size = {(Type == ClipType.Text ? Text?.Length ?? 0 : Bytes.Length)} }}";
}
