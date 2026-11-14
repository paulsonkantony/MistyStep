using System.Net;
using MistyStep.Core.Auth;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Auth;

public class AuthLockoutTests
{
    private static readonly IPAddress Attacker = IPAddress.Parse("192.168.1.66");

    private readonly FakeClock _clock = new();

    [Fact]
    public void Four_failures_do_not_lock()
    {
        var lockout = new AuthLockout(_clock);
        for (var i = 0; i < 4; i++) lockout.RecordFailure(Attacker);
        Assert.False(lockout.IsLockedOut(Attacker));
    }

    [Fact]
    public void Five_failures_within_a_minute_lock()
    {
        var lockout = new AuthLockout(_clock);
        for (var i = 0; i < 5; i++)
        {
            lockout.RecordFailure(Attacker);
            _clock.Advance(10_000);
        }

        Assert.True(lockout.IsLockedOut(Attacker));
        Assert.False(lockout.IsLockedOut(IPAddress.Parse("192.168.1.67")));
    }

    [Fact]
    public void Failures_spread_over_more_than_a_minute_do_not_lock()
    {
        var lockout = new AuthLockout(_clock);
        for (var i = 0; i < 10; i++)
        {
            lockout.RecordFailure(Attacker);
            _clock.Advance(15_000); // only 4 failures fall inside any 60 s window
        }

        Assert.False(lockout.IsLockedOut(Attacker));
    }

    [Fact]
    public void Lock_releases_after_five_minutes()
    {
        var lockout = new AuthLockout(_clock);
        for (var i = 0; i < 5; i++) lockout.RecordFailure(Attacker);

        _clock.Advance(5 * 60_000 - 1);
        Assert.True(lockout.IsLockedOut(Attacker));
        _clock.Advance(1);
        Assert.False(lockout.IsLockedOut(Attacker));
    }

    [Fact]
    public void Ipv4_mapped_address_shares_the_lockout()
    {
        var lockout = new AuthLockout(_clock);
        for (var i = 0; i < 5; i++) lockout.RecordFailure(Attacker.MapToIPv6());
        Assert.True(lockout.IsLockedOut(Attacker));
    }

    [Fact]
    public void Entries_are_capped_at_1000_evicting_the_oldest()
    {
        var lockout = new AuthLockout(_clock);
        var first = IPAddress.Parse("10.0.0.1");
        for (var i = 0; i < 5; i++) lockout.RecordFailure(first);
        Assert.True(lockout.IsLockedOut(first));

        for (var i = 0; i < 1000; i++)
        {
            _clock.Advance(1);
            lockout.RecordFailure(new IPAddress([10, 1, (byte)(i >> 8), (byte)i]));
        }

        Assert.Equal(1000, lockout.Count);
        Assert.False(lockout.IsLockedOut(first));
    }
}

public class DeviceRateLimiterTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void Twenty_requests_per_second_are_allowed_and_the_21st_is_refused()
    {
        var limiter = new DeviceRateLimiter(_clock);
        for (var i = 0; i < 20; i++) Assert.True(limiter.TryAcquire("d_1"));
        Assert.False(limiter.TryAcquire("d_1"));
    }

    [Fact]
    public void Window_resets_after_one_second()
    {
        var limiter = new DeviceRateLimiter(_clock);
        for (var i = 0; i < 21; i++) limiter.TryAcquire("d_1");
        _clock.Advance(999);
        Assert.False(limiter.TryAcquire("d_1"));
        _clock.Advance(1);
        Assert.True(limiter.TryAcquire("d_1"));
    }

    [Fact]
    public void Limits_are_per_device()
    {
        var limiter = new DeviceRateLimiter(_clock);
        for (var i = 0; i < 20; i++) limiter.TryAcquire("d_1");
        Assert.False(limiter.TryAcquire("d_1"));
        Assert.True(limiter.TryAcquire("d_2"));
    }
}
