using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Veil.Auth.Domain;
using Veil.Auth.Domain.Enums;
using Veil.Auth.Infrastructure.Persistence;
using Veil.Auth.Infrastructure.Security;
using Wiaoj.Security;

namespace Veil.Auth.Infrastructure;

/// <summary>
/// Creates the default admin account when the user table is empty. The
/// password comes from <c>Auth:AdminPassword</c>; when unset a random one is
/// generated and logged exactly once — change it after first login.
/// </summary>
public sealed class AdminSeeder(
    IDbContextFactory<AuthDbContext> dbFactory,
    IOptions<AuthOptions> options, 
    ISecretProtector<EmailSecretContext> protector,
    ILogger<AdminSeeder> logger) : IHostedService {

    public async Task StartAsync(CancellationToken cancellationToken) {
        AuthOptions auth = options.Value;

        try {
            await using AuthDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

            // Eğer veritabanında zaten kullanıcı varsa seed işlemini atla.
            if(await db.Users.AnyAsync(cancellationToken))
                return;

            if(string.IsNullOrWhiteSpace(auth.AdminEmail) || string.IsNullOrWhiteSpace(auth.AdminPassword)) {
                logger.LogCritical("Admin seed aborted: 'Auth:AdminEmail' or 'Auth:AdminPassword' configuration is missing. Please provide them to create the initial administrator account.");
                return;
            }

            Result<HexString> hashResult = User.GenerateEmailHash(auth.AdminEmail);
            if(hashResult.IsFailure) {
                logger.LogError("Admin seed failed: {Error}", hashResult.FirstError.Description);
                return;
            }
            HexString emailHash = hashResult.Value;

            EncryptedSecret<EmailSecretContext> encryptedEmail = protector.Protect(auth.AdminEmail);

            using Secret<char> password = Secret<char>.Parse(auth.AdminPassword);

            Result<User> admin = User.Create(
               auth.AdminEmail,
               encryptedEmail,
               emailHash,
               "Administrator",
               Pbkdf2PasswordHasher.Hash(password),
               UserRole.Admin);

            if(admin.IsFailure) {
                logger.LogError("Admin seed failed: {Error}", admin.FirstError.Description);
                return;
            }

            await db.Users.AddAsync(admin.Value, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Seeded initial admin account successfully for {EncryptedEmail}.", auth.AdminEmail);
        }
        catch(Exception ex) {
            logger.LogError(ex, "Admin seed skipped: auth schema unavailable or an unexpected error occurred.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}