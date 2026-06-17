using Microsoft.EntityFrameworkCore;
using Tyto;
using Veil.Certificates.IntegrationEvents;
using Veil.EdgeNodes.Contracts.IntegrationEvents;
using Veil.Zones.Contracts.IntegrationEvents;
using Veil.Zones.Domain.Enums;
using Veil.Zones.Infrastructure.Persistence;

namespace Veil.Api.ConfigSync;

/// <summary>
/// Bus subscriptions driving the config push loop. Both events resolve to
/// the same coalescing signal: the loop's per-node snapshot-hash dedupe
/// makes the wake-up idempotent — a zone change re-pushes everyone, a fresh
/// node registration only actually pushes to the node without a hash entry.
/// </summary>
public sealed class ZoneConfigChangedHandler(
    ConfigPushSignal signal,
    ILogger<ZoneConfigChangedHandler> logger) : IEventHandler<ZoneConfigChanged> {

    public ValueTask HandleAsync(IMessageContext<ZoneConfigChanged> context, CancellationToken cancellationToken = default) {
        logger.LogDebug("Zone {ZoneId} config changed; waking push loop", context.Message.ZoneId);
        signal.Notify();
        return ValueTask.CompletedTask;
    }
}

public sealed class CertificateIssuedHandler(
    ConfigPushSignal signal,
    IDbContextFactory<ZonesDbContext> zonesDbFactory,
    ILogger<CertificateIssuedHandler> logger) : IEventHandler<CertificateIssued> {

    public async ValueTask HandleAsync(IMessageContext<CertificateIssued> context, CancellationToken cancellationToken = default) {
        string hostname = context.Message.Hostname;
        logger.LogInformation("Certificate for {Hostname} issued; pushing TLS material to edge nodes", hostname);

        // A provisioning zone for this hostname was waiting on its certificate
        // — issuance completes provisioning, so flip it Active. Paused/Active
        // zones are left untouched.
        await using ZonesDbContext db = await zonesDbFactory.CreateDbContextAsync(cancellationToken);
        Veil.Zones.Domain.Zone? zone = await db.Zones
            .FirstOrDefaultAsync(z => z.Hostname.Value == hostname && z.Status == ZoneStatus.Provisioning,
                cancellationToken);
        if(zone is not null) {
            zone.Activate();
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Zone {Hostname} activated after certificate issuance", hostname);
        }

        signal.Notify();
    }
}

public sealed class CertificateRevokedHandler(
    ConfigPushSignal signal,
    ILogger<CertificateRevokedHandler> logger) : IEventHandler<CertificateRevoked> {

    public ValueTask HandleAsync(IMessageContext<CertificateRevoked> context, CancellationToken cancellationToken = default) {
        logger.LogInformation("Certificate for {Hostname} revoked; re-pushing edge config",
            context.Message.Hostname);
        signal.Notify();
        return ValueTask.CompletedTask;
    }
}

public sealed class EdgeNodeRegisteredHandler(
    ConfigPushSignal signal,
    ILogger<EdgeNodeRegisteredHandler> logger) : IEventHandler<EdgeNodeRegistered> {

    public ValueTask HandleAsync(IMessageContext<EdgeNodeRegistered> context, CancellationToken cancellationToken = default) {
        logger.LogInformation("Edge node {NodeId} registered; pushing initial config", context.Message.NodeId);
        signal.Notify();
        return ValueTask.CompletedTask;
    }
}
