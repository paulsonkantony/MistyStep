using System.Net;
using MistyStep.Core.Auth;
using MistyStep.Core.Models;
using MistyStep.Core.Network;
using MistyStep.Core.Pairing;
using MistyStep.Core.Settings;
using MistyStep.Core.Store;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Settings;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+C", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'C')]
    [InlineData("ctrl + shift + v", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 'V')]
    [InlineData("Win+F12", HotkeyModifiers.Win, 0x7B)]
    [InlineData("Alt+1", HotkeyModifiers.Alt, '1')]
    [InlineData("Control+Alt+F24", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x87)]
    public void Parses(string text, HotkeyModifiers modifiers, int key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C")]                // no modifier
    [InlineData("Shift+C")]          // Shift alone would capture typing
    [InlineData("Ctrl+Alt")]         // no key
    [InlineData("Ctrl+Alt+C+D")]     // two keys
    [InlineData("Ctrl+Ctrl+C")]      // repeated modifier
    [InlineData("Ctrl+Alt+Space")]   // unsupported key
    [InlineData("Ctrl++C")]
    [InlineData("Ctrl+F25")]
    public void Rejects(string? text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Formats_canonically_and_round_trips()
    {
        Assert.True(Hotkey.TryParse("shift+alt+ctrl+f5", out var hotkey));
        Assert.Equal("Ctrl+Alt+Shift+F5", hotkey!.ToString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var again));
        Assert.Equal(hotkey, again);
        Assert.Equal("Ctrl+Alt+C", Hotkey.Default.ToString());
    }
}

public class PauseControllerTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void Timed_pause_resumes_by_itself()
    {
        var pause = new PauseController(_clock);
        pause.Pause(TimeSpan.FromMinutes(5));
        Assert.Equal(PauseReason.User, pause.Current.Reason);
        Assert.Equal(_clock.MonotonicMs + 300_000, pause.Current.ResumesAtMs);

        _clock.Advance(299_999);
        Assert.True(pause.Current.IsPaused);
        _clock.Advance(1);
        Assert.False(pause.Current.IsPaused);
    }

    [Fact]
    public void Until_resumed_lasts_until_resume()
    {
        var pause = new PauseController(_clock);
        pause.Pause(null);
        _clock.Advance(10 * 3_600_000);
        Assert.True(pause.IsIndefinite);
        pause.Resume();
        Assert.False(pause.Current.IsPaused);
    }

    [Fact]
    public void Locked_screen_pauses_only_when_serving_while_locked_is_off()
    {
        var pause = new PauseController(_clock) { ScreenLocked = true };
        Assert.False(pause.Current.IsPaused);

        pause.ServeWhileLocked = false;
        Assert.Equal(PauseReason.ScreenLocked, pause.Current.Reason);

        pause.ScreenLocked = false;
        Assert.False(pause.Current.IsPaused);
    }

    [Fact]
    public void A_user_pause_is_reported_before_the_lock()
    {
        var pause = new PauseController(_clock) { ScreenLocked = true, ServeWhileLocked = false };
        pause.Pause(TimeSpan.FromHours(1));
        Assert.Equal(PauseReason.User, pause.Current.Reason);
    }

    [Fact]
    public void Status_line_names_the_pause()
    {
        var empty = new ClipSnapshot(null, NoneReason.Empty);
        Assert.Equal("Sharing paused, resumes in 5 min", ShareStatus.Describe(empty, new PauseStatus(PauseReason.User, 300_000), 0));
        Assert.Equal("Sharing paused until you resume", ShareStatus.Describe(empty, new PauseStatus(PauseReason.User, null), 0));
        Assert.Equal("Sharing paused while the screen is locked", ShareStatus.Describe(empty, new PauseStatus(PauseReason.ScreenLocked, null), 0));
        Assert.Equal("Nothing shared yet", ShareStatus.Describe(empty, new PauseStatus(PauseReason.None, null), 0));
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(1, "1 s")]
    [InlineData(45_000, "45 s")]
    [InlineData(60_000, "1 min")]
    [InlineData(61_000, "2 min")]
    [InlineData(3_600_000, "1 h")]
    [InlineData(3_900_000, "1 h 5 min")]
    public void Remaining_rounds_up(long ms, string expected) => Assert.Equal(expected, ShareStatus.Remaining(ms));
}

public class FetchFeedbackAndDiagnosticsTests
{
    private readonly FakeClock _clock = new();

    private (DeviceRegistry Registry, Device Device, string Token) Paired()
    {
        var registry = new DeviceRegistry(_clock);
        var code = registry.CreatePairingCode("http://100.101.102.103:45654", "PC");
        var redeemed = Assert.IsType<RedeemResult.Redeemed>(registry.Redeem(code.Code, IPAddress.Parse("100.64.0.9"), "Mozilla/5.0 (iPad)"));
        Assert.True(SetupCode.TryDecode(redeemed.SetupCode, out var info));
        return (registry, redeemed.Device, info!.Token);
    }

    [Fact]
    public void Delivery_raises_delivered_with_name_and_item()
    {
        var (registry, device, _) = Paired();
        (string Name, string Item)? seen = null;
        registry.Delivered += (name, item) => seen = (name, item);

        registry.RecordDelivery(device.Id, "item-1");
        Assert.Equal((device.Name, "item-1"), seen);

        seen = null;
        registry.RecordDelivery("unknown", "item-2");
        Assert.Null(seen);
    }

    [Fact]
    public void Diagnostics_hold_no_token_hash_or_code()
    {
        var (registry, _, token) = Paired();
        var pending = registry.CreatePairingCode("http://100.101.102.103:45654", "PC");
        var hash = registry.ToRecords().Single().TokenHash;

        var text = Diagnostics.Build(new DiagnosticsInputs(
            "0.1.0", "Windows 11", true, null, 45654, true, false,
            new NetworkSnapshot(IPAddress.Parse("100.101.102.103"), "pc.tailnet.ts.net", "PC", []),
            FirewallState.Ok, CaptureMode.Automatic, "Not paused", 10, registry.Devices, _clock.UtcNow));

        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(hash, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pending.Code, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Misty Step 0.1.0", text, StringComparison.Ordinal);
        Assert.Contains("Windows Firewall: ok", text, StringComparison.Ordinal);
        Assert.Contains("Devices (1)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_mark_the_failing_check_and_skip_later_ones()
    {
        var text = Diagnostics.Build(new DiagnosticsInputs(
            "0.1.0", "Windows 11", true, "Port 45654 is already in use", 45654, true, false,
            NetworkSnapshot.Empty("PC"), FirewallState.MissingRule, CaptureMode.Manual, "Not paused", 0, [], _clock.UtcNow));

        Assert.Contains("Clipboard listener: ok", text, StringComparison.Ordinal);
        Assert.Contains("Sharing server: FAIL (Port 45654 is already in use)", text, StringComparison.Ordinal);
        Assert.Contains("Windows Firewall: not checked", text, StringComparison.Ordinal);
        Assert.Contains("lasts until replaced", text, StringComparison.Ordinal);
    }
}
