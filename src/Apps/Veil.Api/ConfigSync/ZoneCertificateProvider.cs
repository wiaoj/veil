using Microsoft.EntityFrameworkCore;
using Veil.Certificates.Domain;
using Veil.Certificates.Domain.Enums;
using Veil.Certificates.Infrastructure.Persistence;
using Veil.Zones.EdgeConfig;
using Wiaoj.Security;

namespace Veil.Api.ConfigSync;

/// <summary>
/// Loads Active certificates and decrypts their private keys for inclusion
/// in edge config snapshots. Decryption is handled by
/// <see cref="ISecretProtector{PrivateKeySecretContext}"/>.
/// </summary>
public sealed class ZoneCertificateProvider(
    IDbContextFactory<CertificatesDbContext> certsDbFactory,
    ISecretProtector<PrivateKeySecretContext> protector,
    ILogger<ZoneCertificateProvider> logger) {

    public async Task<IReadOnlyDictionary<string, EdgeZoneTlsConfig>> GetActiveCertificatesAsync(
        CancellationToken cancellationToken) {

        await using CertificatesDbContext db = await certsDbFactory.CreateDbContextAsync(cancellationToken);
        var active = await db.Certificates
            .Where(c => c.Status == CertificateStatus.Active
                && c.ChainPem != null && c.EncryptedPrivateKey != null)
            .ToListAsync(cancellationToken);

        Dictionary<string, EdgeZoneTlsConfig> map = new(active.Count);
        foreach(var cert in active) {
            try {
                using Secret<byte> plainKey = protector.Unprotect(cert.EncryptedPrivateKey!.Value);
                string privateKeyPem = plainKey.Expose(bytes =>
                    System.Text.Encoding.UTF8.GetString(bytes));
                map[cert.Hostname] = new EdgeZoneTlsConfig(cert.ChainPem!, privateKeyPem);
            }
            catch(Exception ex) {
                // One undecryptable key must not block snapshot distribution
                // for the other zones.
                logger.LogWarning("Private key for {Hostname} could not be decrypted: {Error}",
                    cert.Hostname, ex.Message);
            }
        }

        return map;
    }
}
