using System.Collections.Concurrent;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Bounded in-memory ring of recent incidents, newest first. The dashboard reads
/// this; it is the fast, process-local "live" view. Durability is provided by
/// <see cref="IIncidentArchive"/> (PostgreSQL): incidents are persisted as they
/// are raised and the ring is rehydrated from the archive on startup.
/// </summary>
public sealed class IncidentStore(int capacity) {
    private readonly ConcurrentQueue<TrafficIncident> _incidents = new();
    private int _count;

    public void Add(TrafficIncident incident) {
        this._incidents.Enqueue(incident);
        if(Interlocked.Increment(ref this._count) > capacity && this._incidents.TryDequeue(out _))
            Interlocked.Decrement(ref this._count);
    }

    public IReadOnlyList<TrafficIncident> Recent(int limit) =>
        this._incidents.Reverse().Take(limit).ToArray();
}
