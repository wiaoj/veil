using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Wiaoj.Extensions;
using Wiaoj.Modulith;

namespace Veil.EdgeNodes;

public sealed class EdgeNodesModule : IWebModule {
    public string Name => nameof(EdgeNodesModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        // Shares the single PostgreSQL database; isolation is the
        // edge_nodes schema, not a separate connection string.
        string? connectionString = configuration.GetConnectionString("Default");

        services.AddDdd(ddd => {
            ddd.AddEntityFrameworkCore<EdgeNodesDbContext>(efcore => {
                efcore.ConfigureOutbox(outbox => {
                    outbox.InitialDelay = 5.Seconds();
                    outbox.PollingInterval = 5.Minutes().WithJitter(Jitter.Minimal);
                });
            });

            ddd.AddTytoIntegration<EdgeNodesModule>(ServiceLifetime.Singleton);
        });

        services.AddDbContextFactory<EdgeNodesDbContext>((sp, options) => {
            options.UseNpgsql(connectionString);
            options.UseDddInterceptors<EdgeNodesDbContext>(sp);
        });

        // Narrow auth surface consumed by internal endpoints and external
        // hosts (analytics ingest) instead of the persistence internals.
        services.AddSingleton<Contracts.IEdgeNodeTokenVerifier, EdgeNodeTokenVerifier>();
    }

    public Task ConfigureAsync(IApplicationBuilder app) {
        if(app is IEndpointRouteBuilder rb) {
            rb.MapEndpoints<EdgeNodesModule>();
        }

        return Task.CompletedTask;
    }
}
