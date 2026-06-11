using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Wiaoj.Modulith;

namespace Veil.EdgeNodes;

public sealed class EdgeNodesModule : IWebModule {
    public string Name => nameof(EdgeNodesModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        // Shares the single PostgreSQL database; isolation is the
        // edge_nodes schema, not a separate connection string.
        string? connectionString = configuration.GetConnectionString("Default");

        // Same outbox → Tyto chain as ZoneModule (see comment there).
        services.AddDdd(ddd => ddd
            .AddEntityFrameworkCore<EdgeNodesDbContext>(efcore => efcore.ConfigureOutbox(outbox => {
                outbox.InitialDelay = TimeSpan.FromSeconds(5);
                outbox.PollingInterval = TimeSpan.FromSeconds(5);
            }))
            .AddTytoIntegration(ServiceLifetime.Singleton, typeof(EdgeNodesModule).Assembly));

        services.AddDbContextFactory<EdgeNodesDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .UseDddInterceptors<EdgeNodesDbContext>(sp));
    }

    public Task ConfigureAsync(IApplicationBuilder app) {
        if(app is IEndpointRouteBuilder rb) {
            rb.MapEndpoints<EdgeNodesModule>();
        }

        return Task.CompletedTask;
    }
}
