using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Drives the intelligence loop: once per interval it sweeps the in-memory
/// analyzer for anomalies, sends each to Claude for triage, records the incident,
/// and — when configured for full-auto and confidence clears the bar — applies
/// the suggested rule (otherwise stages it in shadow mode). Off the ingest hot
/// path entirely, so analysis never adds request latency.
/// </summary>
public sealed class TrafficAnalysisService(
    ITrafficAnalyzer analyzer,
    IAnalystClient analyst,
    IRuleApplier ruleApplier,
    Tyto.IBus bus,
    IncidentStore store,
    TimeProvider timeProvider,
    IOptions<IntelligenceOptions> options,
    ILogger<TrafficAnalysisService> logger) : BackgroundService {

    private readonly IntelligenceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        logger.LogInformation(
            "AI traffic analysis started: interval {Interval}s, triage {Triage}, auto-apply {Auto}",
            this._options.IntervalSeconds,
            string.IsNullOrWhiteSpace(this._options.AnthropicApiKey) ? "disabled (no API key)" : this._options.Model,
            this._options.AutoApplyRules ? $"≥{this._options.AutoApplyMinConfidence:P0}" : "shadow-only");

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(this._options.IntervalSeconds));
        while(await timer.WaitForNextTickAsync(stoppingToken)) {
            try {
                await RunSweepAsync(stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {
                return;
            }
            catch(Exception ex) {
                logger.LogWarning(ex, "Traffic analysis sweep failed");
            }
        }
    }

    private async Task RunSweepAsync(CancellationToken cancellationToken) {
        IReadOnlyList<TrafficIncident> incidents = analyzer.Sweep(timeProvider.GetUtcNow());

        foreach(TrafficIncident incident in incidents) {
            incident.Verdict = await analyst.AnalyzeAsync(incident, cancellationToken);
            incident.Action = await DecideAndActAsync(incident, cancellationToken);
            store.Add(incident);
            // Fan out to the alerting sinks (webhook, SIEM) over the bus; each
            // subscribes independently and best-effort, so a sink never blocks
            // the loop. In-memory transport today (sinks live in this process).
            await bus.PublishAsync(new IncidentRaised(incident), cancellationToken);

            logger.LogInformation(
                "Anomaly in zone {Zone} (score {Score}): {Classification} — {Action}",
                incident.Zone, incident.AnomalyScore,
                incident.Verdict?.Classification ?? incident.Classification, incident.Action);
        }
    }

    private async Task<IncidentAction> DecideAndActAsync(TrafficIncident incident, CancellationToken cancellationToken) {
        // Prefer the LLM's rule when triage ran; otherwise use the deterministic
        // rule the analyzer derived from the signals (the ML-only path).
        SuggestedRule? rule = incident.Verdict?.SuggestedRule ?? incident.SuggestedRule;
        if(rule is null)
            return IncidentAction.None;

        // Confidence comes from the LLM when present, else from the anomaly score.
        double confidence = incident.Verdict?.Confidence ?? (incident.AnomalyScore / 100.0);

        // Full-auto enforces only above the confidence floor; everything else is
        // staged in shadow mode so a false positive can't block real users.
        bool enforce = this._options.AutoApplyRules && confidence >= this._options.AutoApplyMinConfidence;
        await ruleApplier.ApplyAsync(incident.Zone, rule, shadow: !enforce, cancellationToken);
        return enforce ? IncidentAction.Enforced : IncidentAction.Shadowed;
    }
}
