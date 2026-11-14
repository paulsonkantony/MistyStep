using MistyStep.Core.Capture;
using MistyStep.Core.Models;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Capture;

public class ClipDeciderTests
{
    private static readonly CaptureContext Auto = new(Paused: false, CaptureMode.Automatic, HotkeyTriggered: false);

    private static readonly CaptureLimits Limits = new()
    {
        MaxTextBytes = 100,
        MaxImageBytes = 1000,
        ExcludedApps = ["KeePass.exe", "1Password.exe"],
    };

    private readonly FakeClock _clock = new();

    private CaptureResult Decide(FakeRawClip clip, CaptureContext? context = null, CaptureLimits? limits = null) =>
        new ClipDecider(_clock).Decide(clip, context ?? Auto, limits ?? Limits);

    private static ClipItem AssertText(CaptureResult result, string expected)
    {
        var text = Assert.IsType<CaptureResult.Text>(result);
        Assert.Equal(ClipType.Text, text.Item.Type);
        Assert.Equal(expected, text.Item.Text);
        Assert.Equal("text/plain", text.Item.Mime);
        return text.Item;
    }

    private static void AssertIgnored(CaptureResult result, IgnoreReason reason) =>
        Assert.Equal(reason, Assert.IsType<CaptureResult.Ignored>(result).Reason);

    // --- Expected results by source (one test per row) ---

    [Fact]
    public void Notepad_browser_word_text_with_rtf_and_html_gives_plain_text()
    {
        var clip = new FakeRawClip().Text("hello world").Marker("Rich Text Format").Marker("HTML Format");
        var item = AssertText(Decide(clip), "hello world");
        Assert.Equal(_clock.MonotonicMs, item.CapturedAt);
        Assert.Equal(32, item.Id.Length);
    }

    [Fact]
    public void Excel_cells_text_plus_bitmap_gives_text()
    {
        var clip = new FakeRawClip().Text("A1\tB1\r\n").Bytes(ClipboardFormats.Dib, TestImages.FakeDib());
        AssertText(Decide(clip), "A1\tB1\n");
        Assert.DoesNotContain(ClipboardFormats.Dib, clip.Reads);
    }

    [Fact]
    public void Browser_copy_image_png_dib_html_gives_png_unchanged()
    {
        var png = TestImages.PngHeader(1920, 1080);
        var clip = new FakeRawClip()
            .Bytes(ClipboardFormats.Png, png)
            .Bytes(ClipboardFormats.Dib, TestImages.FakeDib())
            .Marker("HTML Format");

        var image = Assert.IsType<CaptureResult.Image>(Decide(clip));
        var candidate = Assert.IsType<ImageCandidate.PngBytes>(image.Candidate);
        Assert.Equal(png, candidate.Bytes);
        Assert.Equal(1920, candidate.Width);
        Assert.Equal(1080, candidate.Height);
        Assert.DoesNotContain(ClipboardFormats.Dib, clip.Reads);
    }

    [Fact]
    public void Screenshot_bitmap_only_gives_dib_image()
    {
        var clip = new FakeRawClip().Bytes(ClipboardFormats.Dib, TestImages.FakeDib());
        var candidate = Assert.IsType<ImageCandidate.Dib>(Assert.IsType<CaptureResult.Image>(Decide(clip)).Candidate);
        Assert.False(candidate.IsV5);
    }

    [Fact]
    public void Screenshot_prefers_dibv5_over_dib()
    {
        var clip = new FakeRawClip()
            .Bytes(ClipboardFormats.Dib, TestImages.FakeDib())
            .Bytes(ClipboardFormats.DibV5, TestImages.FakeDib(80));
        var candidate = Assert.IsType<ImageCandidate.Dib>(Assert.IsType<CaptureResult.Image>(Decide(clip)).Candidate);
        Assert.True(candidate.IsV5);
        Assert.Equal(80, candidate.Bytes.Length);
    }

    [Fact]
    public void Screenshot_with_png_uses_png()
    {
        var clip = new FakeRawClip()
            .Bytes(ClipboardFormats.DibV5, TestImages.FakeDib())
            .Bytes(ClipboardFormats.Png, TestImages.PngHeader(10, 10));
        Assert.IsType<ImageCandidate.PngBytes>(Assert.IsType<CaptureResult.Image>(Decide(clip)).Candidate);
    }

    [Theory]
    [InlineData(@"C:\pics\a.png", "image/png")]
    [InlineData(@"C:\pics\a.JPG", "image/jpeg")]
    [InlineData(@"C:\pics\a.jpeg", "image/jpeg")]
    [InlineData(@"C:\pics\a.gif", "image/gif")]
    [InlineData(@"C:\pics\a.webp", "image/webp")]
    public void Explorer_one_image_file_gives_that_file(string path, string mime)
    {
        var clip = new FakeRawClip().Files((path, 500));
        var file = Assert.IsType<ImageCandidate.File>(Assert.IsType<CaptureResult.Image>(Decide(clip)).Candidate);
        Assert.Equal(path, file.Path);
        Assert.Equal(mime, file.Mime);
        Assert.False(file.ConvertToPng);
    }

    [Fact]
    public void Explorer_one_bmp_file_is_converted_to_png()
    {
        var clip = new FakeRawClip().Files((@"C:\pics\a.bmp", 50_000_000));
        var file = Assert.IsType<ImageCandidate.File>(Assert.IsType<CaptureResult.Image>(Decide(clip)).Candidate);
        Assert.True(file.ConvertToPng);
        Assert.Equal("image/png", file.Mime);
    }

    [Fact]
    public void Explorer_several_files_is_ignored() =>
        AssertIgnored(Decide(new FakeRawClip().Files((@"C:\a.png", 10), (@"C:\b.png", 10))), IgnoreReason.NoUsableContent);

    [Fact]
    public void Explorer_non_image_file_is_ignored() =>
        AssertIgnored(Decide(new FakeRawClip().Files((@"C:\doc.pdf", 10))), IgnoreReason.NoUsableContent);

    [Fact]
    public void Explorer_image_file_over_limit_is_ignored_with_hint()
    {
        var result = Decide(new FakeRawClip().Files((@"C:\big.png", 1001)));
        AssertIgnored(result, IgnoreReason.ImageTooLarge);
        Assert.True(((CaptureResult.Ignored)result).ShowHint);
    }

    [Fact]
    public void Explorer_image_file_exactly_at_limit_is_accepted() =>
        Assert.IsType<CaptureResult.Image>(Decide(new FakeRawClip().Files((@"C:\big.png", 1000))));

    [Fact]
    public void Password_manager_text_with_marker_is_ignored()
    {
        var clip = new FakeRawClip().Text("hunter2").Marker(ClipboardFormats.ExcludeFromMonitor);
        AssertIgnored(Decide(clip), IgnoreReason.Secret);
        Assert.DoesNotContain(ClipboardFormats.UnicodeText, clip.Reads);
    }

    [Fact]
    public void Copying_the_same_text_twice_gives_new_ids()
    {
        var first = AssertText(Decide(new FakeRawClip().Text("same")), "same");
        var second = AssertText(Decide(new FakeRawClip().Text("same")), "same");
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Empty_clipboard_is_ignored() =>
        AssertIgnored(Decide(new FakeRawClip()), IgnoreReason.NoUsableContent);

    // --- Secrecy markers (one per marker) ---

    [Fact]
    public void Marker_exclude_from_monitor_processing() =>
        AssertIgnored(Decide(new FakeRawClip().Text("x").Marker(ClipboardFormats.ExcludeFromMonitor)), IgnoreReason.Secret);

    [Fact]
    public void Marker_clipboard_viewer_ignore() =>
        AssertIgnored(Decide(new FakeRawClip().Text("x").Marker(ClipboardFormats.ViewerIgnore)), IgnoreReason.Secret);

    [Theory]
    [InlineData(ClipboardFormats.CanIncludeInHistory)]
    [InlineData(ClipboardFormats.CanUploadToCloud)]
    public void Marker_dword_zero_is_secret(string format) =>
        AssertIgnored(Decide(new FakeRawClip().Text("x").Dword(format, 0)), IgnoreReason.Secret);

    [Theory]
    [InlineData(ClipboardFormats.CanIncludeInHistory)]
    [InlineData(ClipboardFormats.CanUploadToCloud)]
    public void Marker_dword_one_is_not_secret(string format) =>
        AssertText(Decide(new FakeRawClip().Text("x").Dword(format, 1)), "x");

    [Fact]
    public void Marker_present_but_unreadable_is_treated_as_secret() =>
        AssertIgnored(Decide(new FakeRawClip().Text("x").Unreadable(ClipboardFormats.CanIncludeInHistory)), IgnoreReason.Secret);

    [Fact]
    public void Markers_are_ignored_when_honoring_is_off()
    {
        var clip = new FakeRawClip().Text("x").Marker(ClipboardFormats.ExcludeFromMonitor);
        AssertText(Decide(clip, limits: Limits with { HonorSecrecyMarkers = false }), "x");
    }

    // --- Steps 1 and 3 ---

    [Fact]
    public void Paused_ignores_everything() =>
        AssertIgnored(Decide(new FakeRawClip().Text("x"), Auto with { Paused = true }), IgnoreReason.Paused);

    [Fact]
    public void Manual_mode_without_hotkey_is_ignored() =>
        AssertIgnored(Decide(new FakeRawClip().Text("x"), new CaptureContext(false, CaptureMode.Manual, false)), IgnoreReason.ManualMode);

    [Fact]
    public void Manual_mode_with_hotkey_captures() =>
        AssertText(Decide(new FakeRawClip().Text("x"), new CaptureContext(false, CaptureMode.Manual, true)), "x");

    [Theory]
    [InlineData("KeePass.exe")]
    [InlineData("keepass.EXE")]
    [InlineData("1Password.exe")]
    public void Excluded_app_is_ignored_case_insensitively(string owner)
    {
        var result = Decide(new FakeRawClip().Text("x").Owner(owner));
        AssertIgnored(result, IgnoreReason.ExcludedApp);
        Assert.False(((CaptureResult.Ignored)result).ShowHint);
    }

    [Fact]
    public void Null_owner_is_not_excluded() => AssertText(Decide(new FakeRawClip().Text("x").Owner(null)), "x");

    [Fact]
    public void Other_owner_is_not_excluded() => AssertText(Decide(new FakeRawClip().Text("x").Owner("notepad.exe")), "x");

    // --- Text rules through the decider ---

    [Fact]
    public void Whitespace_only_text_falls_through_to_image()
    {
        var clip = new FakeRawClip().Text("  \r\n\t ").Bytes(ClipboardFormats.Dib, TestImages.FakeDib());
        Assert.IsType<CaptureResult.Image>(Decide(clip));
    }

    [Fact]
    public void Whitespace_only_text_alone_is_ignored() =>
        AssertIgnored(Decide(new FakeRawClip().Text("   ")), IgnoreReason.NoUsableContent);

    [Fact]
    public void Text_exactly_at_limit_is_accepted() =>
        AssertText(Decide(new FakeRawClip().Text(new string('a', 100))), new string('a', 100));

    [Fact]
    public void Text_one_byte_over_limit_is_ignored_with_hint()
    {
        var result = Decide(new FakeRawClip().Text(new string('a', 101)));
        AssertIgnored(result, IgnoreReason.TextTooLarge);
        Assert.True(((CaptureResult.Ignored)result).ShowHint);
    }

    [Fact]
    public void Text_limit_counts_utf8_bytes_not_chars()
    {
        // "é" is 2 UTF-8 bytes: 50 chars = 100 bytes fits, 51 chars = 102 bytes does not.
        AssertText(Decide(new FakeRawClip().Text(new string('é', 50))), new string('é', 50));
        AssertIgnored(Decide(new FakeRawClip().Text(new string('é', 51))), IgnoreReason.TextTooLarge);
    }

    [Fact]
    public void Text_size_is_measured_after_normalising()
    {
        // 50 CRLF pairs = 100 chars raw, 50 after conversion; trailing NUL dropped.
        var raw = string.Concat(Enumerable.Repeat("\r\n", 50)) + "z\0";
        Assert.IsType<CaptureResult.Text>(Decide(new FakeRawClip().Text(raw), limits: Limits with { MaxTextBytes = 51 }));
    }

    [Fact]
    public void Text_url_sets_IsUrl()
    {
        Assert.True(AssertText(Decide(new FakeRawClip().Text("https://example.com/a?b=c")), "https://example.com/a?b=c").IsUrl);
        Assert.False(AssertText(Decide(new FakeRawClip().Text("see https://example.com")), "see https://example.com").IsUrl);
    }

    [Fact]
    public void Invalid_png_falls_back_to_dib()
    {
        var clip = new FakeRawClip()
            .Bytes(ClipboardFormats.Png, [1, 2, 3, 4])
            .Bytes(ClipboardFormats.Dib, TestImages.FakeDib());
        Assert.IsType<ImageCandidate.Dib>(Assert.IsType<CaptureResult.Image>(Decide(clip)).Candidate);
    }
}
