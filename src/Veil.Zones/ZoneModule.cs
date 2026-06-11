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

        services.AddDbContextFactory<ZonesDbContext>(options => options.UseNpgsql(connectionString));
    }

    public Task ConfigureAsync(IApplicationBuilder app) {
        if(app is IEndpointRouteBuilder rb) {
            rb.MapEndpoints<ZoneModule>();
        }

        return Task.CompletedTask;
    }
}
