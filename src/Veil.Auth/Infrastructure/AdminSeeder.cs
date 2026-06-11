using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Veil.Auth.Domain;
using Veil.Auth.Domain.Enums;
using Veil.Auth.Infrastructure.Persistence;
using Veil.Auth.Infrastructure.Security;

namespace Veil.Auth.Infrastructure;

/// <summary>
/// Creates the default admin account when the user table is empty. The
/// password comes from <c>Auth:AdminPassword</c>; when unset a random one is
/// generated and logged exactly once — change it after first login.
/// </summary>
public sealed class AdminSeeder(
    IDbContextFactory<AuthDbContext> dbFactory,
    IOptions<AuthOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminSeeder> logger) : IHostedService {

    public async Task StartAsync(CancellationToken cancellationToken) {
        AuthOptions auth = options.Value;

        try {
            await using AuthDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

            if(await db.Users.AnyAsync(cancellationToken))
                return;

            string? password = auth.AdminPassword;
            bool generated = string.IsNullOrEmpty(password);
            if(generated)
                password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

            Result<User> admin = User.Create(
                auth.AdminEmail,
                "Administrator",
                Pbkdf2PasswordHasher.Hash(password!),
                UserRole.Admin,
                timeProvider.GetUtcNow());

            if(admin.IsFailure) {
                logger.LogError("Admin seed failed: {Error}", admin.FirstError.Description);
                return;
            }

            await db.Users.AddAsync(admin.Value, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            if(generated) {
                logger.LogWarning(
                    "Seeded admin user {Email} with generated password: {Password} — change it after first login",
                    auth.AdminEmail, password);
            }
            else {
                logger.LogInformation("Seeded admin user {Email}", auth.AdminEmail);
            }
        }
        catch(Exception ex) {
            // Schema may not be migrated yet; auth simply has no users until
            // the next start after `dotnet ef database update`.
            logger.LogError(ex, "Admin seed skipped: auth schema unavailable");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}
