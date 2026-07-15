using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Veil.EdgeNodes.Domain;

namespace Veil.Api.ConfigSync;

/// <summary>
/// Resolves the addresses a registered node is currently reachable at.
///
/// A registered <see cref="EdgeNode"/> conflates two very different facts:
/// *who* is allowed to receive config (its token hash — durable, auditable,
/// revocable) and *where* it is right now (its address — ephemeral the moment
/// the fleet is dynamic). This abstraction splits them: identity stays in
/// PostgreSQL, location is resolved per push.
///
/// That split is what makes the Kubernetes deployment work at all. There, every
/// DaemonSet pod shares one identity (one Secret → one registered node) but
/// there are N pods on N ephemeral IPs, so a single stored address can only ever
/// reach one of them.
/// </summary>
public interface IEdgeNodeLocator {
    /// <summary>
    /// Addresses to push this node's config to. Empty means "nothing reachable
    /// right now" — the caller skips the node rather than treating it as failed.
    /// </summary>
    Task<IReadOnlyList<Uri>> ResolveAsync(EdgeNode node, CancellationToken cancellationToken);
}

/// <summary>
/// The address recorded at registration. Correct — and simplest — whenever the
/// node does not move: VMs, bare metal, docker-compose.
/// </summary>
public sealed class StaticEdgeNodeLocator : IEdgeNodeLocator {
    public Task<IReadOnlyList<Uri>> ResolveAsync(EdgeNode node, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Uri>>([node.Address]);
}

/// <summary>
/// Every A record behind a DNS name. Against a Kubernetes headless Service
/// (<c>clusterIP: None</c>) that is exactly the set of *ready* pod IPs, which is
/// why this needs no Kubernetes API access, no RBAC and no client library — just
/// DNS. Kubernetes already knows which pods exist; re-registering them into our
/// own table would only create a second, disagreeing source of truth.
/// </summary>
public sealed class DnsEdgeNodeLocator(
    IOptions<ConfigSyncOptions> options,
    ILogger<DnsEdgeNodeLocator> logger) : IEdgeNodeLocator {

    private readonly DiscoveryOptions _discovery = options.Value.Discovery;

    public async Task<IReadOnlyList<Uri>> ResolveAsync(EdgeNode node, CancellationToken cancellationToken) {
        if(string.IsNullOrWhiteSpace(this._discovery.DnsName)) {
            logger.LogWarning("Discovery mode is Dns but no DnsName is configured; nothing to push to");
            return [];
        }

        try {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(this._discovery.DnsName, cancellationToken);
            return [.. addresses.Select(ToUri)];
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch(Exception ex) {
            // A resolver blip must not wedge the push loop; the reconcile pass
            // retries shortly.
            logger.LogWarning(ex, "Resolving {DnsName} failed", this._discovery.DnsName);
            return [];
        }
    }

    private Uri ToUri(IPAddress address) {
        // IPv6 literals need brackets in a URI authority.
        string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return new Uri($"{this._discovery.Scheme}://{host}:{this._discovery.Port}");
    }
}

/// <summary>
/// Self-registered, TTL'd entries from Redis: nodes write
/// <c>{prefix}{id} = {"address": "..."}</c> with an expiry and keep renewing it.
/// A node that dies stops renewing and the key simply expires — no heartbeat
/// table, no reaper, and no write amplification against the primary database.
///
/// Redis is already a dependency here and already provides exactly this
/// primitive: <see cref="RedisPushCoordinator"/> holds the leader lease with
/// <c>SET NX PX</c>. Standing up Consul to answer "which edges exist" would add
/// a Raft cluster to operate for a capability we already have.
/// </summary>
public sealed class RedisEdgeNodeLocator(
    IConnectionMultiplexer redis,
    IOptions<ConfigSyncOptions> options,
    ILogger<RedisEdgeNodeLocator> logger) : IEdgeNodeLocator {

    private readonly DiscoveryOptions _discovery = options.Value.Discovery;

    public Task<IReadOnlyList<Uri>> ResolveAsync(EdgeNode node, CancellationToken cancellationToken) {
        try {
            IDatabase db = redis.GetDatabase();
            string pattern = $"{this._discovery.RedisKeyPrefix}*";

            List<Uri> addresses = [];
            // A node's registration lives on any one server; scan every endpoint
            // so this also works against a replica set.
            foreach(System.Net.EndPoint endpoint in redis.GetEndPoints()) {
                IServer server = redis.GetServer(endpoint);
                if(server.IsReplica || !server.IsConnected)
                    continue;

                foreach(RedisKey key in server.Keys(pattern: pattern, pageSize: 250)) {
                    RedisValue value = db.StringGet(key);
                    if(value.IsNullOrEmpty)
                        continue;   // expired between SCAN and GET — treat as gone
                    if(TryParse(value!, out Uri? uri))
                        addresses.Add(uri!);
                }
            }

            return Task.FromResult<IReadOnlyList<Uri>>(addresses);
        }
        catch(Exception ex) {
            logger.LogWarning(ex, "Reading self-registered nodes from Redis failed");
            return Task.FromResult<IReadOnlyList<Uri>>([]);
        }
    }

    /// <summary>Accepts either a bare address or a small JSON registration.</summary>
    private bool TryParse(string value, out Uri? uri) {
        uri = null;
        string? address = value.TrimStart().StartsWith('{')
            ? ReadAddressField(value)
            : value;

        if(string.IsNullOrWhiteSpace(address))
            return false;

        // Bare host:port entries get the configured scheme.
        string candidate = address.Contains("://", StringComparison.Ordinal)
            ? address
            : $"{this._discovery.Scheme}://{address}";

        return Uri.TryCreate(candidate, UriKind.Absolute, out uri);
    }

    private static string? ReadAddressField(string json) {
        try {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("address", out JsonElement el) ? el.GetString() : null;
        }
        catch(JsonException) {
            return null;
        }
    }
}
