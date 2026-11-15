using System.Globalization;
using MistyStep.Core.Models;

namespace MistyStep.Core.Store;

/// <summary>The tray status line and tooltip: type and age of the shared item, never its content.</summary>
public static class ShareStatus
{
    public static string Describe(ClipSnapshot snapshot, bool paused, long nowMs)
    {
        if (paused) return "Sharing paused";
        if (snapshot.Item is not { } item)
            return snapshot.Reason == NoneReason.Expired ? "Nothing shared (expired)" : "Nothing shared yet";

        var type = item.Type == ClipType.Text ? (item.IsUrl ? "link" : "text") : "image";
        return $"Sharing: {type}, {Age(nowMs - item.CapturedAt)}";
    }

    public static string Age(long elapsedMs)
    {
        var seconds = Math.Max(0, elapsedMs) / 1000;
        return seconds switch
        {
            < 60 => string.Create(CultureInfo.InvariantCulture, $"{seconds} s ago"),
            < 3600 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min ago"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600} h ago"),
        };
    }
}
