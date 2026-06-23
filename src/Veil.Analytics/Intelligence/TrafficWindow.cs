using Veil.Analytics.Ingestion;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Per-zone in-memory accumulator for the current scoring interval. Writes come
/// from the ingest hot path (<see cref="Observe"/>); the analysis loop calls
/// <see cref="SnapshotAndReset"/> once per interval. All access is guarded by a
/// per-zone lock — this never touches ClickHouse, so scoring is "anlık" (live).
/// </summary>
internal sealed class TrafficWindow(int maxTrackedKeys) {
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _ipCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pathCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _asnCounts = new(StringComparer.Ordinal);

    private long _requests;
    private long _blocked;       // verdict "block" or "ip_reputation"
    private long _challenged;    // verdict "challenge"
    private long _rateLimited;   // verdict "rate_limited"

    // Per-interval request-rate history, fed to the ML detector. Touched only by
    // the single-threaded sweep, so no lock is needed.
    private readonly Queue<double> _rateHistory = new();

    public DateTimeOffset LastIncidentUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Appends a rate sample and returns the bounded rolling series.</summary>
    public IReadOnlyList<double> PushRate(double rate, int max) {
        this._rateHistory.Enqueue(rate);
        while(this._rateHistory.Count > max)
            this._rateHistory.Dequeue();
        return this._rateHistory.ToArray();
    }

    public void Observe(RequestLogRow row) {
        lock(this._gate) {
            this._requests++;
            switch(row.Verdict) {
                case "block" or "ip_reputation": this._blocked++; break;
                case "challenge": this._challenged++; break;
                case "rate_limited": this._rateLimited++; break;
            }
            Bump(this._ipCounts, row.ClientIp);
            Bump(this._pathCounts, row.Path);
            // asn 0 means "unknown" (no ASN MMDB or a miss) — don't track it,
            // or a no-GeoIP node would show a fake "100% from AS0" concentration.
            if(row.Asn != 0)
                Bump(this._asnCounts, row.Asn.ToString());
        }
    }

    /// <summary>Reads the interval's totals, then clears them for the next interval.</summary>
    public TrafficSnapshot SnapshotAndReset(int intervalSeconds) {
        lock(this._gate) {
            TrafficCount[] topIps = Top(this._ipCounts, 5);
            TrafficCount[] topPaths = Top(this._pathCounts, 5);
            TrafficCount[] topAsns = Top(this._asnCounts, 5);

            TrafficSnapshot snapshot = new(
                Requests: this._requests,
                Blocked: this._blocked,
                Challenged: this._challenged,
                RateLimited: this._rateLimited,
                DistinctIps: this._ipCounts.Count,
                RatePerSecond: this._requests / (double)Math.Max(1, intervalSeconds),
                TopIps: topIps,
                TopPaths: topPaths,
                TopAsns: topAsns);

            this._requests = this._blocked = this._challenged = this._rateLimited = 0;
            this._ipCounts.Clear();
            this._pathCounts.Clear();
            this._asnCounts.Clear();
            return snapshot;
        }
    }

    private void Bump(Dictionary<string, int> map, string key) {
        if(string.IsNullOrEmpty(key))
            return;
        if(map.TryGetValue(key, out int count))
            map[key] = count + 1;
        else if(map.Count < maxTrackedKeys)   // cap keeps a flood from exhausting memory
            map[key] = 1;
    }

    private static TrafficCount[] Top(Dictionary<string, int> map, int n) =>
        map.OrderByDescending(static kv => kv.Value)
           .Take(n)
           .Select(static kv => new TrafficCount(kv.Key, kv.Value))
           .ToArray();
}

/// <summary>Immutable per-interval view of one zone's traffic.</summary>
internal sealed record TrafficSnapshot(
    long Requests,
    long Blocked,
    long Challenged,
    long RateLimited,
    int DistinctIps,
    double RatePerSecond,
    TrafficCount[] TopIps,
    TrafficCount[] TopPaths,
    TrafficCount[] TopAsns) {

    public double BlockedRatio => (this.Blocked + this.RateLimited) / (double)Math.Max(1, this.Requests);
    public double ChallengedRatio => this.Challenged / (double)Math.Max(1, this.Requests);
    public double TopIpShare => this.TopIps.Length == 0 ? 0 : this.TopIps[0].Count / (double)Math.Max(1, this.Requests);
    public double TopAsnShare => this.TopAsns.Length == 0 ? 0 : this.TopAsns[0].Count / (double)Math.Max(1, this.Requests);
}
