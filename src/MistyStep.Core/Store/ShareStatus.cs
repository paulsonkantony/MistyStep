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

    /// <summary>The status line with the pause reason and time left, e.g. "Sharing paused, resumes in 4 min".</summary>
    public static string Describe(ClipSnapshot snapshot, Settings.PauseStatus pause, long nowMs) => pause.Reason switch
    {
        Settings.PauseReason.User when pause.ResumesAtMs is { } at => $"Sharing paused, resumes in {Remaining(at - nowMs)}",
        Settings.PauseReason.User => "Sharing paused until you resume",
        Settings.PauseReason.ScreenLocked => "Sharing paused while the screen is locked",
        _ => Describe(snapshot, paused: false, nowMs),
    };

    /// <summary>Time left, rounded up: "45 s", "4 min", "1 h 5 min".</summary>
    public static string Remaining(long ms)
    {
        var seconds = (Math.Max(0, ms) + 999) / 1000;
        if (seconds < 60) return string.Create(CultureInfo.InvariantCulture, $"{seconds} s");
        var minutes = (seconds + 59) / 60;
        return minutes < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min")
            : minutes % 60 == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h")
                : string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60} min");
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
