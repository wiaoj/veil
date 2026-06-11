using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Veil.Auth.Infrastructure;
using Veil.Auth.Infrastructure.Persistence;
using Veil.Auth.Infrastructure.Security;
using Wiaoj.Modulith;

namespace Veil.Auth;

public sealed class AuthModule : IWebModule {
    public string Name => nameof(AuthModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        string? connectionString = configuration.GetConnectionString("Default");

        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        // Same outbox → dispatch chain as the other modules.
        services.AddDdd(ddd => ddd
            .AddEntityFrameworkCore<AuthDbContext>(efcore => efcore.ConfigureOutbox(outbox => {
                outbox.InitialDelay = TimeSpan.FromSeconds(5);
                outbox.PollingInterval = TimeSpan.FromSeconds(5);
            })));

        services.AddDbContextFactory<AuthDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .UseDddInterceptors<AuthDbContext>(sp));

        services.AddSingleton<JwtTokenService>();
        services.AddHostedService<AdminSeeder>();

        // Without a signing key the module cannot validate or issue tokens —
        // authentication stays unregistered and every endpoint remains open.
        // Acceptable only for throwaway dev setups.
        string? signingKey = configuration[$"{AuthOptions.SectionName}:SigningKey"];
        if(string.IsNullOrEmpty(signingKey))
            return;

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => {
                options.TokenValidationParameters = new TokenValidationParameters {
                    ValidIssuer = configuration[$"{AuthOptions.SectionName}:Issuer"] ?? new AuthOptions().Issuer,
                    ValidAudience = configuration[$"{AuthOptions.SectionName}:Audience"] ?? new AuthOptions().Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, _ => { });

        // Everything is protected by default; public endpoints (login,
        // refresh) and internally-authenticated ones (edge config pull)
        // opt out explicitly with AllowAnonymous.
        services.AddAuthorization(options => {
            options.FallbackPolicy = new AuthorizationPolicyBuilder(
                    JwtBearerDefaults.AuthenticationScheme,
                    ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build();
        });
    }

    public Task ConfigureAsync(IApplicationBuilder app) {
        if(app is IEndpointRouteBuilder rb) {
            rb.MapEndpoints<AuthModule>();
        }

        return Task.CompletedTask;
    }
}
