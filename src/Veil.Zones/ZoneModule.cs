using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veil.Zones.Infrastructure.Persistence;
using Wiaoj.Extensions;
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
        services.AddDdd(ddd => {
            ddd.AddEntityFrameworkCore<ZonesDbContext>(efcore => {
                efcore.ConfigureOutbox(outbox => {
                    outbox.InitialDelay = 5.Seconds();
                    outbox.PollingInterval = 5.Minutes().WithJitter(Jitter.Minimal); //ZombieProcess
                });
            });

            ddd.AddTytoIntegration<ZoneModule>(ServiceLifetime.Singleton);
        });
         
        
        services.AddDbContextFactory<ZonesDbContext>((sp, options) => {
                options.UseNpgsql(connectionString);
                options.UseDddInterceptors<ZonesDbContext>(sp);
            });
    }

    public Task ConfigureAsync(IApplicationBuilder app) {
        if(app is IEndpointRouteBuilder rb) {
            rb.MapEndpoints<ZoneModule>();
        }

        return Task.CompletedTask;
    }
}
