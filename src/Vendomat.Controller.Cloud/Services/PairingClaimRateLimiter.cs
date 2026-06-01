using System.Collections.Concurrent;

namespace Vendomat.Controller.Cloud.Services;

public sealed class PairingClaimRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);
    private const int MaxFailedAttempts = 5;

    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);

    public bool IsAllowed(string remoteAddress, Guid machineId, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var key = BuildKey(remoteAddress, machineId);
        if (!_attempts.TryGetValue(key, out var state))
        {
            return true;
        }

        lock (state)
        {
            var now = DateTimeOffset.UtcNow;
            if (state.LockedUntilUtc <= now)
            {
                return true;
            }

            retryAfter = state.LockedUntilUtc - now;
            return false;
        }
    }

    public void RecordSuccess(string remoteAddress, Guid machineId) =>
        _attempts.TryRemove(BuildKey(remoteAddress, machineId), out _);

    public void RecordFailure(string remoteAddress, Guid machineId)
    {
        var key = BuildKey(remoteAddress, machineId);
        var state = _attempts.GetOrAdd(key, _ => new AttemptState());

        lock (state)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - state.WindowStartedUtc > Window)
            {
                state.WindowStartedUtc = now;
                state.FailedAttempts = 0;
            }

            state.FailedAttempts++;
            if (state.FailedAttempts >= MaxFailedAttempts)
            {
                state.LockedUntilUtc = now.Add(Lockout);
                state.FailedAttempts = 0;
                state.WindowStartedUtc = now;
            }
        }
    }

    private static string BuildKey(string remoteAddress, Guid machineId) =>
        $"{remoteAddress.Trim()}|{machineId:N}";

    private sealed class AttemptState
    {
        public DateTimeOffset WindowStartedUtc { get; set; } = DateTimeOffset.UtcNow;
        public int FailedAttempts { get; set; }
        public DateTimeOffset LockedUntilUtc { get; set; } = DateTimeOffset.MinValue;
    }
}
