using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Modulith;

namespace Veil.Zones;

public sealed class ZoneModule : IWebModule {
    public string Name => nameof(ZoneModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        // Modules share one PostgreSQL database; isolation is per-schema
        // (zones, auth, edge_nodes...), not per-connection. A module gets its
        // own connection string only if it is ever extracted into a service.
        string? connectionString = configuration.GetConnectionString("Default");

        // Domain events flow through the transactional outbox to the Tyto
        // bus: SaveChanges persists OutboxMessages in the same transaction,
        // the outbox processor dispatches post-commit, and the scanned
        // IIntegrationEventMapper implementations auto-publish to IBus.
        // Singleton lifetime: contexts come from a singleton factory, so the
        // whole dispatch chain must be resolvable from the root provider.
        services.AddDdd(ddd => ddd
            .AddEntityFrameworkCore<ZonesDbContext>(efcore => efcore.ConfigureOutbox(outbox => {
                // Config changes must reach edge nodes promptly; the default
                // 2-minute warmup delay is far too slow for a control plane.
                outbox.InitialDelay = TimeSpan.FromSeconds(5);
                outbox.PollingInterval = TimeSpan.FromSeconds(5);
            }))
            .AddTytoIntegration(ServiceLifetime.Singleton, typeof(ZoneModule).Assembly));

        services.AddDbContextFactory<ZonesDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .UseDddInterceptors<ZonesDbContext>(sp));
    }

    public Task ConfigureAsync(IApplicationBuilder app) {
        if(app is IEndpointRouteBuilder rb) {
            rb.MapEndpoints<ZoneModule>();
        }

        return Task.CompletedTask;
    }
}
