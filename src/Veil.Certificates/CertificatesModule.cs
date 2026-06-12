using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veil.Certificates.Infrastructure.Persistence;
using Wiaoj.Modulith;

namespace Veil.Certificates;

public sealed class CertificatesModule : IWebModule {
    public string Name => nameof(CertificatesModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        // Shares the single PostgreSQL database; isolation is the
        // certificates schema, not a separate connection string.
        string? connectionString = configuration.GetConnectionString("Default");

        // Same outbox → Tyto chain as ZoneModule (see comment there).
        services.AddDdd(ddd => ddd
            .AddEntityFrameworkCore<CertificatesDbContext>(efcore => efcore.ConfigureOutbox(outbox => {
                outbox.InitialDelay = TimeSpan.FromSeconds(5);
                outbox.PollingInterval = TimeSpan.FromSeconds(5);
            }))
            .AddTytoIntegration<CertificatesModule>(ServiceLifetime.Singleton));

        services.AddDbContextFactory<CertificatesDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .UseDddInterceptors<CertificatesDbContext>(sp));
    }

    public Task ConfigureAsync(IApplicationBuilder app) {
        if(app is IEndpointRouteBuilder rb) {
            rb.MapEndpoints<CertificatesModule>();
        }

        return Task.CompletedTask;
    }
}
