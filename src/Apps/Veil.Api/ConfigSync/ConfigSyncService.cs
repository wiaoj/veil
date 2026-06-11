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
    IObfuscator<RuleId> ruleObfuscator,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    IOptions<ConfigSyncOptions> options,
    ILogger<ConfigSyncService> logger) : BackgroundService {

    public const string HttpClientName = "edge-push";

    // Configurable so a deployment can rename the header / move the reserved
    // path; must match the edge's VEIL_PUSH_SIGNATURE_HEADER and push path.
    private readonly ConfigSyncOptions _options = options.Value;

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>Per-node hash of the last successfully pushed snapshot.</summary>
    private readonly Dictionary<long, string> _lastPushedHash = [];

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

    /// <summary>True when woken by a change signal, false on a reconcile tick.</summary>
    private async Task<bool> WaitForChangeOrReconcileAsync(CancellationToken stoppingToken) {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(ReconcileInterval);
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

        EdgeConfigSnapshot snapshot = EdgeConfigSnapshotBuilder.Build(zones, ruleObfuscator);
        string json = JsonSerializer.Serialize(snapshot);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        string snapshotHash = Sha256Hash.Compute(jsonBytes).ToHexString().ToLower();
        string signature = HmacSha256Hash.Compute(pushKey, jsonBytes).ToHexString().ToLower();

        await using EdgeNodesDbContext nodesDb = await nodesDbFactory.CreateDbContextAsync(cancellationToken);
        List<EdgeNode> nodes = await nodesDb.EdgeNodes
            .Where(n => n.Status != EdgeNodeStatus.Disabled)
            .ToListAsync(cancellationToken);

        bool wroteLog = false;
        foreach(EdgeNode node in nodes) {
            long nodeKey = node.Id.Value.Value;
            if(this._lastPushedHash.TryGetValue(nodeKey, out string? pushed) && pushed == snapshotHash)
                continue;

            (bool succeeded, string? error) = await PushToNodeAsync(node, json, signature, cancellationToken);

            nodesDb.ConfigPushLogs.Add(ConfigPushLog.Record(
                node.Id, succeeded, Truncate(error, 2048), timeProvider.GetUtcNow()));
            wroteLog = true;

            if(succeeded) {
                this._lastPushedHash[nodeKey] = snapshotHash;
                node.MarkSeen(timeProvider.GetUtcNow());
                logger.LogInformation("Config pushed to edge node {Node} ({Address})", node.Name, node.Address);
            }
            else {
                logger.LogWarning(
                    "Config push to edge node {Node} ({Address}) failed after {Attempts} attempts: {Error}",
                    node.Name, node.Address, RetryDelays.Length, error);
            }
        }

        if(wroteLog)
            await nodesDb.SaveChangesAsync(cancellationToken);
    }

    private async Task<(bool Succeeded, string? Error)> PushToNodeAsync(
        EdgeNode node,
        string json,
        string signature,
        CancellationToken cancellationToken) {
        string url = node.Address.ToString().TrimEnd('/') + this._options.PushPath;
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
