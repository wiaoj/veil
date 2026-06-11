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

        services.AddDbContextFactory<EdgeNodesDbContext>(options => options.UseNpgsql(connectionString));
    }

    public Task ConfigureAsync(IApplicationBuilder app) {
        if(app is IEndpointRouteBuilder rb) {
            rb.MapEndpoints<EdgeNodesModule>();
        }

        return Task.CompletedTask;
    }
}
