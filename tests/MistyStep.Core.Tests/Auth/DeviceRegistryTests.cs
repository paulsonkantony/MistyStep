using System.Net;
using System.Text.Json;
using MistyStep.Core.Auth;
using MistyStep.Core.Config;
using MistyStep.Core.Pairing;
using MistyStep.Core.Serialization;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Auth;

public class DeviceRegistryTests
{
    private const string BaseUrl = "http://100.101.102.103:45654";
    private const string PcName = "MY-LAPTOP";
    private const string IpadSafari = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Version/18.0 Safari/605.1.15";

    private static readonly IPAddress IpadAddress = IPAddress.Parse("100.90.1.2");
    private static readonly IPAddress OtherAddress = IPAddress.Parse("100.90.9.9");

    private readonly FakeClock _clock = new();
    private readonly DeviceRegistry _registry;

    public DeviceRegistryTests() => _registry = new DeviceRegistry(_clock);

    private (Device Device, string Token, string SetupCode) Pair(IPAddress? from = null)
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        var redeemed = Assert.IsType<RedeemResult.Redeemed>(_registry.Redeem(code.Code, from ?? IpadAddress, IpadSafari));
        Assert.True(SetupCode.TryDecode(redeemed.SetupCode, out var info));
        return (redeemed.Device, info!.Token, redeemed.SetupCode);
    }

    // --- Pair, authenticate, revoke, wrong token ---

    [Fact]
    public void Pairing_creates_a_device_and_a_working_setup_code()
    {
        var (device, token, setupCode) = Pair();

        Assert.StartsWith("d_", device.Id);
        Assert.Equal(BaseUrl, device.BaseUrl);
        Assert.Equal("iPad (Nov 14)", device.Name);
        Assert.Equal(0, device.FetchCount);
        Assert.Null(device.LastSeenUtc);
        Assert.Equal(43, token.Length);

        Assert.True(SetupCode.TryDecode(setupCode, out var info));
        Assert.Equal(BaseUrl, info!.Url);
        Assert.Equal(PcName, info.PcName);

        Assert.Equal(device.Id, _registry.Authenticate(token)?.Id);
        Assert.Single(_registry.Devices);
    }

    [Fact]
    public void Pairing_code_is_32_hex_chars_valid_for_ten_minutes()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        Assert.Equal(32, code.Code.Length);
        Assert.True(code.Code.All(char.IsAsciiHexDigitLower));
        Assert.Equal(_clock.MonotonicMs + 10 * 60_000, code.ExpiresAtMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Wrong_token_is_rejected(string? token)
    {
        Pair();
        Assert.Null(_registry.Authenticate(token));
    }

    [Fact]
    public void Each_device_has_its_own_token()
    {
        var a = Pair();
        var b = Pair(OtherAddress);
        Assert.NotEqual(a.Token, b.Token);
        Assert.Equal(a.Device.Id, _registry.Authenticate(a.Token)?.Id);
        Assert.Equal(b.Device.Id, _registry.Authenticate(b.Token)?.Id);
    }

    [Fact]
    public void Revoked_device_cannot_authenticate()
    {
        var (device, token, _) = Pair();
        var events = new List<RegistryChange>();
        _registry.Changed += events.Add;

        Assert.True(_registry.Revoke(device.Id));
        Assert.Null(_registry.Authenticate(token));
        Assert.Empty(_registry.Devices);
        Assert.Equal([RegistryChange.Structural], events);
        Assert.False(_registry.Revoke(device.Id));
    }

    [Fact]
    public void Revoking_one_device_keeps_the_other()
    {
        var a = Pair();
        var b = Pair(OtherAddress);
        _registry.Revoke(a.Device.Id);
        Assert.Null(_registry.Authenticate(a.Token));
        Assert.NotNull(_registry.Authenticate(b.Token));
    }

    [Fact]
    public void Rename_trims_and_validates()
    {
        var (device, _, _) = Pair();
        Assert.True(_registry.Rename(device.Id, "  Kitchen iPad  "));
        Assert.Equal("Kitchen iPad", _registry.Devices[0].Name);
        Assert.False(_registry.Rename(device.Id, "   "));
        Assert.False(_registry.Rename(device.Id, new string('x', 65)));
        Assert.False(_registry.Rename("d_missing", "x"));
    }

    [Fact]
    public void Seen_and_delivery_are_recorded()
    {
        var (device, _, _) = Pair();
        var events = new List<RegistryChange>();
        _registry.Changed += events.Add;

        _registry.RecordSeen(device.Id);
        _registry.RecordDelivery(device.Id, "item1");

        var updated = _registry.Devices[0];
        Assert.Equal(_clock.UtcNow.UtcDateTime, updated.LastSeenUtc);
        Assert.Equal(1, updated.FetchCount);
        Assert.True(_registry.HasReceived(device.Id, "item1"));
        Assert.False(_registry.HasReceived(device.Id, "item2"));
        Assert.Equal([RegistryChange.Activity, RegistryChange.Activity], events);
    }

    [Fact]
    public void Delivery_is_tracked_per_device()
    {
        var a = Pair();
        var b = Pair(OtherAddress);
        _registry.RecordDelivery(a.Device.Id, "item1");
        Assert.True(_registry.HasReceived(a.Device.Id, "item1"));
        Assert.False(_registry.HasReceived(b.Device.Id, "item1"));
    }

    // --- Persistence ---

    [Fact]
    public void Serialised_config_does_not_contain_the_token()
    {
        var (_, token, setupCode) = Pair();
        var config = new AppConfig { Devices = _registry.ToRecords() };
        var json = JsonSerializer.Serialize(config, CoreJsonContext.Default.AppConfig);

        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        Assert.DoesNotContain(setupCode, json, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))), json, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_round_trip_into_a_new_registry()
    {
        var (device, token, _) = Pair();
        _registry.RecordDelivery(device.Id, "item1");

        var restored = new DeviceRegistry(_clock);
        restored.Load(_registry.ToRecords());

        Assert.Equal(device.Id, restored.Authenticate(token)?.Id);
        Assert.Equal(1, restored.Devices[0].FetchCount);
        Assert.False(restored.HasReceived(device.Id, "item1")); // delivery state is memory only
    }

    [Fact]
    public void Load_skips_invalid_records()
    {
        var good = new DeviceRecord("d_1", "iPad", new string('a', 64), BaseUrl, DateTime.UtcNow, null, 0);
        var badHash = good with { Id = "d_2", TokenHash = "xyz" };
        var duplicateId = good with { TokenHash = new string('b', 64) };
        _registry.Load([good, badHash, duplicateId]);
        Assert.Single(_registry.Devices);
    }

    // --- Pairing codes ---

    [Fact]
    public void Same_address_may_reload_and_gets_the_same_setup_code()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        var first = Assert.IsType<RedeemResult.Redeemed>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));
        _clock.Advance(60_000);
        var reload = Assert.IsType<RedeemResult.Redeemed>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));

        Assert.Equal(first.SetupCode, reload.SetupCode);
        Assert.Equal(first.Device.Id, reload.Device.Id);
        Assert.Single(_registry.Devices);
    }

    [Fact]
    public void Ipv4_mapped_address_counts_as_the_same_caller()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        _registry.Redeem(code.Code, IpadAddress, IpadSafari);
        Assert.IsType<RedeemResult.Redeemed>(_registry.Redeem(code.Code, IpadAddress.MapToIPv6(), IpadSafari));
    }

    [Fact]
    public void Other_address_gets_gone_after_redeem()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        _registry.Redeem(code.Code, IpadAddress, IpadSafari);
        var result = Assert.IsType<RedeemResult.Gone>(_registry.Redeem(code.Code, OtherAddress, IpadSafari));
        Assert.Equal(RedeemFailure.BoundToOtherAddress, result.Failure);
    }

    [Fact]
    public void Code_expires_after_ten_minutes()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        _clock.Advance(10 * 60_000 - 1);
        Assert.Equal(1, _registry.PendingCount);
        _clock.Advance(1);
        var result = Assert.IsType<RedeemResult.Gone>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));
        Assert.Equal(RedeemFailure.UnknownOrExpired, result.Failure);
        Assert.Empty(_registry.Devices);
    }

    [Fact]
    public void Plaintext_token_is_dropped_at_expiry_but_device_stays_paired()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        var redeemed = Assert.IsType<RedeemResult.Redeemed>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));
        SetupCode.TryDecode(redeemed.SetupCode, out var info);

        _clock.Advance(10 * 60_000);

        Assert.IsType<RedeemResult.Gone>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));
        Assert.Equal(0, _registry.PendingCount);
        Assert.NotNull(_registry.Authenticate(info!.Token));
    }

    [Fact]
    public void Unknown_code_is_gone()
    {
        Assert.IsType<RedeemResult.Gone>(_registry.Redeem("0123456789abcdef0123456789abcdef", IpadAddress, null));
        Assert.IsType<RedeemResult.Gone>(_registry.Redeem(null, IpadAddress, null));
    }

    [Fact]
    public void Six_failed_attempts_cancel_every_outstanding_code()
    {
        var a = _registry.CreatePairingCode(BaseUrl, PcName);
        var b = _registry.CreatePairingCode(BaseUrl, PcName);

        for (var i = 0; i < 5; i++) _registry.Redeem("bad" + i, OtherAddress, null);
        Assert.Equal(2, _registry.PendingCount);

        _registry.Redeem("bad5", OtherAddress, null);
        Assert.Equal(0, _registry.PendingCount);
        Assert.IsType<RedeemResult.Gone>(_registry.Redeem(a.Code, IpadAddress, IpadSafari));
        Assert.IsType<RedeemResult.Gone>(_registry.Redeem(b.Code, IpadAddress, IpadSafari));
        Assert.Empty(_registry.Devices);
    }

    [Fact]
    public void Wrong_address_attempts_count_as_failures()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        var pending = _registry.CreatePairingCode(BaseUrl, PcName);
        _registry.Redeem(code.Code, IpadAddress, IpadSafari);
        for (var i = 0; i < 6; i++) _registry.Redeem(code.Code, OtherAddress, null);
        Assert.IsType<RedeemResult.Gone>(_registry.Redeem(pending.Code, IpadAddress, IpadSafari));
        Assert.Single(_registry.Devices); // the already-paired device stays paired
    }

    [Fact]
    public void Failure_count_resets_for_a_fresh_pairing_session()
    {
        for (var i = 0; i < 5; i++) _registry.Redeem("bad" + i, OtherAddress, null);
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        _registry.Redeem("bad-again", OtherAddress, null);
        Assert.IsType<RedeemResult.Redeemed>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));
    }

    [Fact]
    public void Cancel_revokes_a_redeemed_device()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        var redeemed = Assert.IsType<RedeemResult.Redeemed>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));
        SetupCode.TryDecode(redeemed.SetupCode, out var info);

        _registry.CancelPairing(code.Code);

        Assert.Null(_registry.Authenticate(info!.Token));
        Assert.Empty(_registry.Devices);
        Assert.IsType<RedeemResult.Gone>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));
    }

    [Fact]
    public void Cancel_of_unredeemed_code_just_removes_it()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        _registry.CancelPairing(code.Code);
        Assert.Equal(0, _registry.PendingCount);
    }

    [Fact]
    public void Revoke_during_pairing_window_stops_reloads()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        var redeemed = Assert.IsType<RedeemResult.Redeemed>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));
        _registry.Revoke(redeemed.Device.Id);
        Assert.IsType<RedeemResult.Gone>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));
    }

    [Fact]
    public void Results_and_codes_do_not_print_secrets()
    {
        var code = _registry.CreatePairingCode(BaseUrl, PcName);
        var redeemed = Assert.IsType<RedeemResult.Redeemed>(_registry.Redeem(code.Code, IpadAddress, IpadSafari));
        SetupCode.TryDecode(redeemed.SetupCode, out var info);

        Assert.DoesNotContain("MS1.", redeemed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(info!.Token, info.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(code.Code, code.ToString(), StringComparison.Ordinal);
    }

    // --- Device names ---

    [Theory]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X)", "iPad (Nov 14)")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)", "iPhone (Nov 14)")]
    [InlineData(IpadSafari, "iPad (Nov 14)")]
    [InlineData("curl/8.0", "Device (Nov 14)")]
    [InlineData(null, "Device (Nov 14)")]
    public void Device_name_from_user_agent(string? userAgent, string expected) =>
        Assert.Equal(expected, DeviceNamer.FromUserAgent(userAgent, new DateTime(2026, 11, 14, 12, 0, 0)));
}
