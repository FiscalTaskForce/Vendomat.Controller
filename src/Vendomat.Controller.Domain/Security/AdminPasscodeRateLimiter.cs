namespace Vendomat.Controller.Domain.Security;

/// <summary>
/// Throttles admin passcode attempts to defeat brute forcing of the short PIN.
/// After <see cref="MaxAttempts"/> consecutive failures the gate locks for a
/// back-off window that grows with each additional failed burst.
/// Thread-safe; intended to be used as a singleton on the controller.
/// </summary>
public sealed class AdminPasscodeRateLimiter
{
    public const int MaxAttempts = 5;

    private static readonly TimeSpan BaseLockout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    private int _failedAttempts;
    private int _lockoutBursts;
    private DateTimeOffset _lockedUntilUtc = DateTimeOffset.MinValue;

    public AdminPasscodeRateLimiter()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    public AdminPasscodeRateLimiter(Func<DateTimeOffset> clock)
    {
        _clock = clock;
    }

    /// <summary>Time remaining before another attempt is allowed, or zero if unlocked.</summary>
    public TimeSpan RetryAfter
    {
        get
        {
            lock (_gate)
            {
                var remaining = _lockedUntilUtc - _clock();
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    public bool IsLocked => RetryAfter > TimeSpan.Zero;

    /// <summary>
    /// Verifies a passcode attempt while enforcing the lockout. Returns false (without
    /// evaluating the passcode) while locked out.
    /// </summary>
    public bool TryVerify(string? storedHash, string? passcode)
    {
        lock (_gate)
        {
            if (_clock() < _lockedUntilUtc)
            {
                return false;
            }

            if (AdminPasscodeHasher.Verify(storedHash, passcode))
            {
                Reset();
                return true;
            }

            RegisterFailureUnsafe();
            return false;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _failedAttempts = 0;
            _lockoutBursts = 0;
            _lockedUntilUtc = DateTimeOffset.MinValue;
        }
    }

    private void RegisterFailureUnsafe()
    {
        _failedAttempts++;
        if (_failedAttempts < MaxAttempts)
        {
            return;
        }

        _failedAttempts = 0;
        var multiplier = Math.Min(_lockoutBursts, 5);
        var lockout = TimeSpan.FromTicks(BaseLockout.Ticks * (1L << multiplier));
        if (lockout > MaxLockout)
        {
            lockout = MaxLockout;
        }

        _lockoutBursts++;
        _lockedUntilUtc = _clock() + lockout;
    }
}
