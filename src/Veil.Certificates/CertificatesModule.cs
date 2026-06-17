using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veil.Certificates.Infrastructure.Persistence;
using Wiaoj.Extensions;
using Wiaoj.Modulith;

namespace Veil.Certificates;

public sealed class CertificatesModule : IWebModule {
    public string Name => nameof(CertificatesModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        // Shares the single PostgreSQL database; isolation is the
        // certificates schema, not a separate connection string.
        string? connectionString = configuration.GetConnectionString("Default");

        services.AddDdd(ddd => {
            ddd.AddEntityFrameworkCore<CertificatesDbContext>(efcore => {
                efcore.ConfigureOutbox(outbox => {
                    outbox.InitialDelay = 5.Seconds();
                    outbox.PollingInterval = 5.Minutes().WithJitter(Jitter.Minimal);
                });
            });

            ddd.AddTytoIntegration<CertificatesModule>(ServiceLifetime.Singleton);
        });
         
        services.AddDbContextFactory<CertificatesDbContext>((sp, options) => {
            options.UseNpgsql(connectionString);
            options.UseDddInterceptors<CertificatesDbContext>(sp);
        });

        services.Configure<CertificatesOptions>(configuration.GetSection(CertificatesOptions.SectionName));

        services.AddWiaojSecurity()
            .AddManagedProtector<Domain.PrivateKeySecretContext>()
            .AddDataRotator<Domain.PrivateKeySecretContext, Infrastructure.Security.PrivateKeyDataRotator>();
    }

    public Task ConfigureAsync(IApplicationBuilder app) {
        if(app is IEndpointRouteBuilder rb) {
            rb.MapEndpoints<CertificatesModule>();
        }

        return Task.CompletedTask;
    }
}
