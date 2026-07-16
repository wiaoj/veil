using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.Enums;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.EdgeConfig;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Primitives.Cryptography.Hashing;

namespace Veil.Api.ConfigSync;

/// <summary>
/// Pushes zone config snapshots to registered edge nodes whenever the zone
/// configuration changes (signalled by <see cref="ConfigPushSignal"/>, fed
/// by the Tyto event handlers in <see cref="ZoneConfigChangedHandler"/> /
/// <see cref="EdgeNodeRegisteredHandler"/>), plus a periodic reconcile pass
/// so nodes that missed a push converge.
///
/// Push bodies are HMAC-SHA256 signed with the shared
/// <c>ConfigSync:PushHmacKey</c> — the control plane only stores node token
/// hashes, so it cannot authenticate to nodes with their tokens.
///
/// Hosted in Veil.Api for now: the change signal is in-process. Moving this
/// to the standalone Veil.ConfigSync worker requires an outbox (and the Redis
/// retry queue / leader election from the roadmap) — deliberately deferred.
/// </summary>
public sealed class ConfigSyncService(
    ConfigPushSignal signal,
    IDbContextFactory<ZonesDbContext> zonesDbFactory,
    IDbContextFactory<EdgeNodesDbContext> nodesDbFactory,
    ZoneCertificateProvider certificateProvider,
    IObfuscator<RuleId> ruleObfuscator,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    Veil.Shared.Observability.MetricsCollector metrics,
    IPushCoordinator coordinator,
    IEdgeNodeLocator locator,
    Veil.Api.Schemas.ISchemaRegistry schemaRegistry,
    IOptions<ConfigSyncOptions> options,
    ILogger<ConfigSyncService> logger) : BackgroundService {

    private const string PushTotal = "veil_config_push_total";
    private static readonly TimeSpan NotLeaderPoll = TimeSpan.FromSeconds(10);

    public const string HttpClientName = "edge-push";

    // Configurable so a deployment can rename the header / move the reserved
    // path; must match the edge's VEIL_PUSH_SIGNATURE_HEADER and push path.
    private readonly ConfigSyncOptions _options = options.Value;

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>
    /// Hash of the last snapshot successfully pushed, keyed by *address* rather
    /// than by node: one registered node can resolve to many addresses (a
    /// Kubernetes DaemonSet is one identity across N pods), and each of them has
    /// to be tracked separately or a new pod would be skipped as "already
    /// current". Pruned each sweep to the set of live addresses.
    /// </summary>
    private readonly Dictionary<string, string> _lastPushedHash = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        byte[]? pushKey = ReadPushKey();
        if(pushKey is null) {
            logger.LogWarning(
                "Config sync disabled: ConfigSync:PushHmacKey is not configured (64 hex chars expected)");
            return;
        }

        logger.LogInformation(
            "Config sync started (debounce {Debounce}, reconcile every {Reconcile})",
            DebounceWindow, ReconcileInterval);

        while(!stoppingToken.IsCancellationRequested) {
            bool signalled = await WaitForChangeOrReconcileAsync(stoppingToken);
            if(stoppingToken.IsCancellationRequested) break;

            // Only the elected leader pushes; standbys idle until they win
            // the lock (e.g. the leader dies and its lease expires).
            if(!await SafeIsLeaderAsync(stoppingToken)) {
                try { await Task.Delay(NotLeaderPoll, stoppingToken); }
                catch(OperationCanceledException) { break; }
                continue;
            }

            if(signalled) {
                // Coalesce bursts (e.g. several rules edited in a row).
                await Task.Delay(DebounceWindow, stoppingToken);
            }

            try {
                await PushToAllNodesAsync(pushKey, stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {
                break;
            }
            catch(Exception ex) {
                logger.LogError(ex, "Config sync cycle failed");
            }
        }
    }

    private async Task<bool> SafeIsLeaderAsync(CancellationToken stoppingToken) {
        try {
            return await coordinator.IsLeaderAsync(stoppingToken);
        }
        catch(Exception ex) {
            // A Redis blip must not wedge the loop; treat as non-leader and
            // retry on the next tick.
            logger.LogWarning(ex, "Leader check failed; standing by");
            return false;
        }
    }

    /// <summary>
    /// Waits for a change signal, the reconcile interval, or the next due
    /// retry (whichever comes first). Returns true when woken by a signal.
    /// </summary>
    private async Task<bool> WaitForChangeOrReconcileAsync(CancellationToken stoppingToken) {
        TimeSpan wait = ReconcileInterval;
        TimeSpan? retryIn = null;
        try { retryIn = await coordinator.TimeUntilNextRetryAsync(stoppingToken); }
        catch(Exception ex) { logger.LogDebug(ex, "Retry-queue peek failed"); }
        if(retryIn is { } due && due < wait)
            wait = due;

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(wait);
        try {
            await signal.WaitAsync(timeout.Token);
            return true;
        }
        catch(OperationCanceledException) {
            return false;
        }
    }

    private async Task PushToAllNodesAsync(byte[] pushKey, CancellationToken cancellationToken) {
        await using ZonesDbContext zonesDb = await zonesDbFactory.CreateDbContextAsync(cancellationToken);
        List<Zone> zones = await zonesDb.Zones
            .AsNoTracking()
            .Include(z => z.Rules)
            .ToListAsync(cancellationToken);

        EdgeConfigSnapshot snapshot = EdgeConfigSnapshotBuilder.Build(zones, ruleObfuscator,
            await certificateProvider.GetActiveCertificatesAsync(cancellationToken),
            await ResolveSchemasAsync(zones, cancellationToken));
        string json = JsonSerializer.Serialize(snapshot);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        string snapshotHash = Sha256Hash.Compute(jsonBytes).ToHexString().ToLower();
        string signature = HmacSha256Hash.Compute(pushKey, jsonBytes).ToHexString().ToLower();

        await using EdgeNodesDbContext nodesDb = await nodesDbFactory.CreateDbContextAsync(cancellationToken);
        List<EdgeNode> nodes = await nodesDb.EdgeNodes
            .Where(n => n.Status != EdgeNodeStatus.Disabled)
            .ToListAsync(cancellationToken);

        bool wroteLog = false;
        HashSet<string> liveAddresses = [];

        foreach(EdgeNode node in nodes) {
            long nodeKey = node.Id.Value.Value;

            // Identity comes from the database; location is resolved now, because
            // in a dynamic fleet the address recorded at registration is stale the
            // moment a pod is rescheduled.
            IReadOnlyList<Uri> addresses = await locator.ResolveAsync(node, cancellationToken);
            if(addresses.Count == 0) {
                logger.LogWarning("No address resolved for edge node {Node}; skipping this sweep", node.Name);
                continue;
            }

            bool allSucceeded = true;
            bool attemptedAny = false;
            string? firstError = null;

            foreach(Uri address in addresses) {
                string addressKey = address.ToString();
                liveAddresses.Add(addressKey);

                if(this._lastPushedHash.TryGetValue(addressKey, out string? pushed) && pushed == snapshotHash)
                    continue;   // this address already holds this snapshot

                attemptedAny = true;
                (bool ok, string? error) = await PushToAddressAsync(address, json, signature, cancellationToken);

                if(ok) {
                    this._lastPushedHash[addressKey] = snapshotHash;
                }
                else {
                    this._lastPushedHash.Remove(addressKey);
                    allSucceeded = false;
                    firstError ??= $"{address}: {error}";
                }
            }

            if(!attemptedAny)
                continue;   // every address was already up to date

            nodesDb.ConfigPushLogs.Add(ConfigPushLog.Record(
                node.Id, allSucceeded, Truncate(firstError, 2048), timeProvider.GetUtcNow()));
            wroteLog = true;

            metrics.IncrementCounter(PushTotal, "Config push attempts to edge nodes, by result.",
                labels: ("result", allSucceeded ? "success" : "failure"));

            if(allSucceeded) {
                node.MarkSeen(timeProvider.GetUtcNow());
                await coordinator.ClearRetryAsync(nodeKey, cancellationToken);
                logger.LogInformation(
                    "Config pushed to edge node {Node} ({Count} address(es))", node.Name, addresses.Count);
            }
            else {
                // Any address left behind means the node as a whole is not
                // converged — retry the node, and the per-address dedupe above
                // keeps the ones that did succeed from being pushed again.
                await coordinator.EnqueueRetryAsync(nodeKey, cancellationToken);
                logger.LogWarning(
                    "Config push to edge node {Node} failed after {Attempts} attempts: {Error}",
                    node.Name, RetryDelays.Length, firstError);
            }
        }

        // Addresses that no longer resolve (a pod went away) must not linger in
        // the dedupe map — otherwise it grows without bound across pod churn.
        if(liveAddresses.Count > 0) {
            foreach(string stale in this._lastPushedHash.Keys.Where(k => !liveAddresses.Contains(k)).ToList())
                this._lastPushedHash.Remove(stale);
        }

        if(wroteLog)
            await nodesDb.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves every distinct <c>body_schema</c> reference used by any rule to
    /// its concrete schema from the registry (Vaultify), so the snapshot builder
    /// can embed it. A reference that fails to resolve is simply left out — the
    /// builder then drops that rule fail-open rather than shipping validation the
    /// edge can't perform.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, JsonElement>> ResolveSchemasAsync(
        IReadOnlyList<Zone> zones, CancellationToken cancellationToken) {
        if(!schemaRegistry.IsEnabled)
            return EmptySchemas;

        HashSet<Schemas.SchemaRef> refs = [.. zones
            .SelectMany(z => z.Rules)
            .SelectMany(r => r.Conditions)
            .OfType<BodySchemaMatchCondition>()
            .Select(c => new Schemas.SchemaRef(c.Subject, c.Version))];

        if(refs.Count == 0)
            return EmptySchemas;

        Dictionary<string, JsonElement> resolved = [];
        foreach(Schemas.SchemaRef reference in refs) {
            string? raw = await schemaRegistry.ResolveRawAsync(reference, cancellationToken);
            if(raw is null)
                continue;
            try {
                using JsonDocument doc = JsonDocument.Parse(raw);
                resolved[EdgeConfigSnapshotBuilder.SchemaKey(reference.Subject, reference.Version)] =
                    doc.RootElement.Clone();
            }
            catch(JsonException ex) {
                logger.LogWarning(ex, "Resolved schema {Subject}@{Version} is not valid JSON",
                    reference.Subject, reference.Version);
            }
        }
        return resolved;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptySchemas =
        new Dictionary<string, JsonElement>();

    private async Task<(bool Succeeded, string? Error)> PushToAddressAsync(
        Uri address,
        string json,
        string signature,
        CancellationToken cancellationToken) {
        string url = address.ToString().TrimEnd('/') + this._options.PushPath;
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        string? lastError = null;

        for(int attempt = 0; attempt < RetryDelays.Length; attempt++) {
            try {
                using HttpRequestMessage request = new(HttpMethod.Post, url) {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation(this._options.SignatureHeader, signature);

                using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                if(response.IsSuccessStatusCode)
                    return (true, null);

                lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
                throw;
            }
            catch(Exception ex) {
                lastError = ex.Message;
            }

            if(attempt < RetryDelays.Length - 1)
                await Task.Delay(RetryDelays[attempt], cancellationToken);
        }

        return (false, lastError);
    }

    private byte[]? ReadPushKey() {
        string? hex = this._options.PushHmacKey;
        if(string.IsNullOrWhiteSpace(hex)) return null;
        try {
            byte[] key = Convert.FromHexString(hex);
            return key.Length == 32 ? key : null;
        }
        catch(FormatException) {
            return null;
        }
    }

    private static string? Truncate(string? value, int max) {
        return value is { Length: > 0 } && value.Length > max ? value[..max] : value;
    }
}
