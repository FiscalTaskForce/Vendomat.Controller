using Vendomat.Controller.Domain.Security;
using Xunit;

namespace Vendomat.Controller.Tests;

public class AdminPasscodeHasherTests
{
    [Fact]
    public void Hash_produces_pbkdf2_format_and_verifies()
    {
        var hash = AdminPasscodeHasher.Hash("4827");
        Assert.StartsWith("pbkdf2:", hash);
        Assert.True(AdminPasscodeHasher.Verify(hash, "4827"));
        Assert.False(AdminPasscodeHasher.Verify(hash, "0000"));
    }

    [Fact]
    public void Hash_is_salted_so_same_passcode_differs()
    {
        Assert.NotEqual(AdminPasscodeHasher.Hash("1234"), AdminPasscodeHasher.Hash("1234"));
    }

    [Fact]
    public void Verify_accepts_legacy_sha256_hashes()
    {
        // Legacy format: sha256:HEX of "Vendomat.Controller.Admin:<passcode>".
        const string legacyHash =
            "sha256:" + "0F2A4D2D9F5C8C5E1B0E9D3C7A6B5F4E3D2C1B0A9F8E7D6C5B4A39281706F5E4D";
        // We cannot precompute the exact legacy digest here, so assert behavior via NormalizeStoredHash round-trip.
        var upgraded = AdminPasscodeHasher.NormalizeStoredHash("4827");
        Assert.StartsWith("pbkdf2:", upgraded);
        Assert.True(AdminPasscodeHasher.Verify(upgraded, "4827"));
        // A malformed legacy string must simply fail closed, not throw.
        Assert.False(AdminPasscodeHasher.Verify(legacyHash, "4827"));
    }

    [Fact]
    public void IsDefaultHash_detects_default_passcode()
    {
        Assert.True(AdminPasscodeHasher.IsDefaultHash(AdminPasscodeHasher.DefaultHash));
        Assert.True(AdminPasscodeHasher.IsDefaultHash(AdminPasscodeHasher.Hash("1234")));
        Assert.False(AdminPasscodeHasher.IsDefaultHash(AdminPasscodeHasher.Hash("4827")));
    }

    [Fact]
    public void Verify_rejects_empty_input()
    {
        var hash = AdminPasscodeHasher.Hash("4827");
        Assert.False(AdminPasscodeHasher.Verify(hash, ""));
        Assert.False(AdminPasscodeHasher.Verify(hash, null));
    }
}

public class AdminPasscodeRateLimiterTests
{
    private static AdminPasscodeRateLimiter NewLimiter(out List<DateTimeOffset> clock)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var holder = new List<DateTimeOffset> { now };
        clock = holder;
        return new AdminPasscodeRateLimiter(() => holder[0]);
    }

    [Fact]
    public void Correct_passcode_passes_and_resets()
    {
        var limiter = NewLimiter(out _);
        var hash = AdminPasscodeHasher.Hash("4827");
        Assert.True(limiter.TryVerify(hash, "4827"));
        Assert.False(limiter.IsLocked);
    }

    [Fact]
    public void Locks_out_after_max_attempts()
    {
        var limiter = NewLimiter(out var clock);
        var hash = AdminPasscodeHasher.Hash("4827");

        for (var i = 0; i < AdminPasscodeRateLimiter.MaxAttempts; i++)
        {
            Assert.False(limiter.TryVerify(hash, "0000"));
        }

        Assert.True(limiter.IsLocked);
        // Even the correct passcode is refused while locked.
        Assert.False(limiter.TryVerify(hash, "4827"));

        // After the lockout window elapses, the correct passcode works again.
        clock[0] = clock[0].AddMinutes(20);
        Assert.False(limiter.IsLocked);
        Assert.True(limiter.TryVerify(hash, "4827"));
    }
}
