using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wiaoj.Modulith;

namespace Veil.Infrastructure.Security;

public sealed class SecurityModule : IModule {
    public string Name => nameof(SecurityModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        string? connectionString = configuration.GetConnectionString("Default");

        services.AddDbContextFactory<SecurityDbContext>((sp, options) => {
            options.UseNpgsql(connectionString);
        });

        // Şifreleme Motoru ve Master Key yapılandırması
        services.AddWiaojSecurity(opts => {
            opts.RotationInterval = TimeSpan.FromDays(90);
            opts.CheckInterval = TimeSpan.FromHours(6);
            opts.KeySizeInBits = 256;
            opts.AutoRotateData = true;
        })
        .AddEnvironmentMasterKey("VEIL_MASTER_KEY")
        .AddEntityFrameworkKeyStore<SecurityDbContext>();
    }
}