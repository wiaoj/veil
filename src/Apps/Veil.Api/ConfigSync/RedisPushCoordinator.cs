using StackExchange.Redis;

namespace Veil.Api.ConfigSync;

/// <summary>
/// Redis-backed coordination for multi-replica ConfigSync.
///
/// Leader election: a single key (<c>veil:configsync:leader</c>) is acquired
/// with <c>SET NX PX</c> and renewed each check; only the holder pushes, so
/// at most one replica drives the loop at a time. If the leader dies its key
/// expires and another replica takes over within the TTL.
///
/// Retry queue: failed node pushes go into a sorted set scored by their next
/// attempt time (exponential backoff), letting the loop wake before the
/// 5-minute reconcile to retry just the nodes that need it.
/// </summary>
public sealed class RedisPushCoordinator(IConnectionMultiplexer redis, TimeProvider timeProvider, ILogger<RedisPushCoordinator> logger)
    : IPushCoordinator {
    private const string LeaderKey = "veil:configsync:leader";
    private const string RetryKey = "veil:configsync:retry";
    private static readonly TimeSpan LeaderTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(3)];

    private readonly string _instanceId = Guid.NewGuid().ToString("n");

    // Renew the lock iff we still own it, otherwise the holder may have
    // changed and we must not extend someone else's lease.
    private const string RenewScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";

    public async Task<bool> IsLeaderAsync(CancellationToken cancellationToken) {
        IDatabase db = redis.GetDatabase();
        // Renew if we hold it; otherwise try to take a free slot.
        long renewed = (long)await db.ScriptEvaluateAsync(
            RenewScript,
            [LeaderKey],
            [this._instanceId, (long)LeaderTtl.TotalMilliseconds]);
        if(renewed == 1)
            return true;

        bool acquired = await db.StringSetAsync(LeaderKey, this._instanceId, LeaderTtl, When.NotExists);
        if(acquired)
            logger.LogInformation("ConfigSync leadership acquired by instance {Instance}", this._instanceId);
        return acquired;
    }

    public async Task EnqueueRetryAsync(long nodeKey, CancellationToken cancellationToken) {
        IDatabase db = redis.GetDatabase();
        // Track attempt count in the member's companion key for backoff.
        string attemptKey = $"veil:configsync:attempt:{nodeKey}";
        long attempt = await db.StringIncrementAsync(attemptKey);
        await db.KeyExpireAsync(attemptKey, TimeSpan.FromHours(1));
        TimeSpan delay = Backoff[Math.Min((int)attempt - 1, Backoff.Length - 1)];
        double dueMs = timeProvider.GetUtcNow().Add(delay).ToUnixTimeMilliseconds();
        await db.SortedSetAddAsync(RetryKey, nodeKey, dueMs);
    }

    public async Task ClearRetryAsync(long nodeKey, CancellationToken cancellationToken) {
        IDatabase db = redis.GetDatabase();
        await db.SortedSetRemoveAsync(RetryKey, nodeKey);
        await db.KeyDeleteAsync($"veil:configsync:attempt:{nodeKey}");
    }

    public async Task<TimeSpan?> TimeUntilNextRetryAsync(CancellationToken cancellationToken) {
        IDatabase db = redis.GetDatabase();
        SortedSetEntry[] next = await db.SortedSetRangeByRankWithScoresAsync(RetryKey, 0, 0);
        if(next.Length == 0)
            return null;
        double nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        double dueInMs = next[0].Score - nowMs;
        return dueInMs <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(dueInMs);
    }
}
