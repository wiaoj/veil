using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Veil.Analytics.Ingestion;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Always-on traffic detector. Maintains a per-zone <see cref="TrafficWindow"/>
/// fed live from the ingest stream. Each sweep snapshots the window, runs the
/// ML.NET spike detector over the zone's request-rate series, and combines that
/// with deterministic attack signals (enforced block ratio, single-source share)
/// into a score, a classification, and a suggested rule — all with no LLM call.
/// </summary>
public sealed class TrafficAnalyzer(
    MlAnomalyDetector detector,
    IOptions<IntelligenceOptions> options) : ITrafficAnalyzer {

    private readonly IntelligenceOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, TrafficWindow> _zones = new(StringComparer.Ordinal);

    public void Observe(IReadOnlyList<RequestLogRow> batch) {
        foreach(RequestLogRow row in batch) {
            if(string.IsNullOrEmpty(row.Zone))
                continue;
            TrafficWindow window = this._zones.GetOrAdd(row.Zone, _ => new TrafficWindow(this._options.MaxTrackedKeys));
            window.Observe(row);
        }
    }

    public IReadOnlyList<TrafficIncident> Sweep(DateTimeOffset nowUtc) {
        List<TrafficIncident> incidents = [];

        foreach((string zone, TrafficWindow window) in this._zones) {
            TrafficSnapshot snapshot = window.SnapshotAndReset(this._options.IntervalSeconds);
            IReadOnlyList<double> series = window.PushRate(snapshot.RatePerSecond, this._options.RateHistoryLength);
            if(snapshot.Requests == 0)
                continue;

            AnomalyResult ml = detector.Evaluate(series);
            double baseline = series.Count > 1 ? series.Take(series.Count - 1).Average() : snapshot.RatePerSecond;

            bool rateSpike = ml.IsAnomaly && snapshot.RatePerSecond >= this._options.MinRequestsPerSecond;
            bool highBlock = snapshot.Requests >= 50 && snapshot.BlockedRatio >= this._options.BlockedRatioThreshold;
            bool singleSource = snapshot.Requests >= 50 && snapshot.TopIpShare >= this._options.TopIpShareThreshold;

            int score = (rateSpike ? 50 : 0) + (highBlock ? 30 : 0) + (singleSource ? 30 : 0);
            if(score < this._options.IncidentScoreThreshold)
                continue;
            if(nowUtc - window.LastIncidentUtc < TimeSpan.FromSeconds(this._options.CooldownSeconds))
                continue;

            window.LastIncidentUtc = nowUtc;

            List<string> signals = [];
            if(rateSpike)
                signals.Add($"ml_rate_spike ({snapshot.RatePerSecond:F0}/s vs ~{baseline:F0}/s, score {ml.Score:F1})");
            if(highBlock)
                signals.Add($"high_block_ratio ({snapshot.BlockedRatio:P0})");
            if(singleSource)
                signals.Add($"single_source ({snapshot.TopIps[0].Value} = {snapshot.TopIpShare:P0})");

            (string classification, SuggestedRule? rule) = Classify(snapshot, rateSpike, highBlock, singleSource);

            incidents.Add(new TrafficIncident {
                Id = Guid.NewGuid().ToString("n"),
                DetectedAtUtc = nowUtc,
                Zone = zone,
                AnomalyScore = Math.Min(100, score),
                Signals = signals.ToArray(),
                RatePerSecond = snapshot.RatePerSecond,
                BaselineRatePerSecond = Math.Max(0, baseline),
                BlockedRatio = snapshot.BlockedRatio,
                DistinctIps = snapshot.DistinctIps,
                TopIps = snapshot.TopIps,
                TopPaths = snapshot.TopPaths,
                Classification = classification,
                SuggestedRule = rule
            });
        }

        return incidents;
    }

    /// <summary>
    /// Deterministic, LLM-free classification + mitigation. Prefers
    /// <c>challenge</c> over <c>block</c> so a false positive friction-tests a
    /// client rather than hard-blocking it.
    /// </summary>
    private static (string Classification, SuggestedRule? Rule) Classify(
        TrafficSnapshot s, bool rateSpike, bool highBlock, bool singleSource) {

        if(singleSource) {
            string ip = s.TopIps[0].Value;
            // A single IP dominating + lots of enforcement looks like a focused
            // flood/brute-force → challenge that IP.
            return ("single_source_flood", new SuggestedRule("ip", ip, "challenge"));
        }

        if(rateSpike && highBlock) {
            // Distributed spike already tripping rules. If one path dominates,
            // rate-limit it; otherwise no single safe matcher — leave to a human.
            if(s.TopPaths.Length > 0 && s.TopPaths[0].Count >= s.Requests * 0.5)
                return ("path_flood", new SuggestedRule("path_regex", $"^{Regex.Escape(s.TopPaths[0].Value)}$", "rate_limit"));
            return ("http_flood", null);
        }

        if(rateSpike)
            return ("traffic_spike", null);   // could be legitimate — surface, don't act

        return ("anomalous", null);
    }
}
