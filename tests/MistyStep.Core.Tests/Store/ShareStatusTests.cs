using System.Net;
using MistyStep.Core.Auth;
using MistyStep.Core.Models;
using MistyStep.Core.Store;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Store;

public class ShareStatusTests
{
    [Theory]
    [InlineData(0, "0 s ago")]
    [InlineData(12_900, "12 s ago")]
    [InlineData(59_999, "59 s ago")]
    [InlineData(60_000, "1 min ago")]
    [InlineData(3_599_999, "59 min ago")]
    [InlineData(7_200_000, "2 h ago")]
    [InlineData(-5, "0 s ago")]
    public void Age(long ms, string expected) => Assert.Equal(expected, ShareStatus.Age(ms));

    [Fact]
    public void Describes_type_and_age_never_content()
    {
        var text = ClipItem.CreateText("top secret words", 1000);
        var status = ShareStatus.Describe(new ClipSnapshot(text, NoneReason.Empty), paused: false, nowMs: 13_000);
        Assert.Equal("Sharing: text, 12 s ago", status);
        Assert.DoesNotContain("secret", status, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_and_images_are_named()
    {
        var link = ClipItem.CreateText("https://example.com", 0);
        var image = ClipItem.CreateImage(new byte[] { 1 }, "image/png", 1, 1, 0);
        Assert.StartsWith("Sharing: link", ShareStatus.Describe(new ClipSnapshot(link, NoneReason.Empty), false, 0));
        Assert.StartsWith("Sharing: image", ShareStatus.Describe(new ClipSnapshot(image, NoneReason.Empty), false, 0));
    }

    [Fact]
    public void None_and_paused_states()
    {
        Assert.Equal("Nothing shared yet", ShareStatus.Describe(new ClipSnapshot(null, NoneReason.Empty), false, 0));
        Assert.Equal("Nothing shared (expired)", ShareStatus.Describe(new ClipSnapshot(null, NoneReason.Expired), false, 0));
        Assert.Equal("Sharing paused", ShareStatus.Describe(new ClipSnapshot(null, NoneReason.Empty), true, 0));
    }
}

public class PairingStatusTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void Waiting_then_redeemed_then_gone_at_expiry()
    {
        var registry = new DeviceRegistry(_clock);
        var code = registry.CreatePairingCode("http://100.101.102.103:45654", "PC");

        Assert.IsType<PairingStatus.Waiting>(registry.GetPairingStatus(code.Code));

        registry.Redeem(code.Code, IPAddress.Parse("100.90.1.2"), "iPad");
        var redeemed = Assert.IsType<PairingStatus.Redeemed>(registry.GetPairingStatus(code.Code));
        Assert.Null(redeemed.Device.LastSeenUtc);

        registry.RecordSeen(redeemed.Device.Id);
        Assert.NotNull(Assert.IsType<PairingStatus.Redeemed>(registry.GetPairingStatus(code.Code)).Device.LastSeenUtc);

        _clock.Advance(10 * 60_000);
        Assert.IsType<PairingStatus.Gone>(registry.GetPairingStatus(code.Code));
    }

    [Fact]
    public void Cancelled_or_revoked_is_gone()
    {
        var registry = new DeviceRegistry(_clock);
        var code = registry.CreatePairingCode("http://a:1", "PC");
        var device = ((RedeemResult.Redeemed)registry.Redeem(code.Code, IPAddress.Loopback, null)).Device;

        registry.Revoke(device.Id);
        Assert.IsType<PairingStatus.Gone>(registry.GetPairingStatus(code.Code));

        var other = registry.CreatePairingCode("http://a:1", "PC");
        registry.CancelPairing(other.Code);
        Assert.IsType<PairingStatus.Gone>(registry.GetPairingStatus(other.Code));
        Assert.IsType<PairingStatus.Gone>(registry.GetPairingStatus("unknown"));
    }
}
