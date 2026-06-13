namespace Veil.Api.ConfigSync;

/// <summary>
/// Coordinates the config-push loop across replicas. The default
/// <see cref="LocalPushCoordinator"/> is a single-instance no-op; the
/// <see cref="RedisPushCoordinator"/> adds a leader lock (only one replica
/// pushes) and a backoff retry queue (failed nodes are retried sooner than
/// the 5-minute reconcile).
/// </summary>
public interface IPushCoordinator {
    /// <summary>True when this instance may run the push cycle.</summary>
    Task<bool> IsLeaderAsync(CancellationToken cancellationToken);

    /// <summary>Schedules a failed node for an earlier, backed-off retry.</summary>
    Task EnqueueRetryAsync(long nodeKey, CancellationToken cancellationToken);

    /// <summary>Clears any pending retry for a node that just succeeded.</summary>
    Task ClearRetryAsync(long nodeKey, CancellationToken cancellationToken);

    /// <summary>Delay until the earliest due retry, or null if none pending.</summary>
    Task<TimeSpan?> TimeUntilNextRetryAsync(CancellationToken cancellationToken);
}

/// <summary>Single-instance coordinator: always leader, no retry queue.</summary>
public sealed class LocalPushCoordinator : IPushCoordinator {
    public Task<bool> IsLeaderAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public Task EnqueueRetryAsync(long nodeKey, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ClearRetryAsync(long nodeKey, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<TimeSpan?> TimeUntilNextRetryAsync(CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
}
