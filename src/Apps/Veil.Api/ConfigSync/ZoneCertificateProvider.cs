using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Veil.Certificates;
using Veil.Certificates.Domain.Enums;
using Veil.Certificates.Infrastructure.Persistence;
using Veil.Certificates.Infrastructure.Security;
using Veil.Zones.EdgeConfig;

namespace Veil.Api.ConfigSync;

/// <summary>
/// Loads Active certificates and decrypts their private keys for inclusion
/// in edge config snapshots. Returns an empty map when no encryption key is
/// configured — zones are then served plaintext rather than failing the
/// whole snapshot.
/// </summary>
public sealed class ZoneCertificateProvider(
    IDbContextFactory<CertificatesDbContext> certsDbFactory,
    IOptions<CertificatesOptions> options,
    ILogger<ZoneCertificateProvider> logger) {

    public async Task<IReadOnlyDictionary<string, EdgeZoneTlsConfig>> GetActiveCertificatesAsync(
        CancellationToken cancellationToken) {
        PrivateKeyProtector? protector = PrivateKeyProtector.FromHex(options.Value.EncryptionKey);
        if(protector is null)
            return new Dictionary<string, EdgeZoneTlsConfig>();

        await using CertificatesDbContext db = await certsDbFactory.CreateDbContextAsync(cancellationToken);
        var active = await db.Certificates
            .AsNoTracking()
            .Where(c => c.Status == CertificateStatus.Active
                && c.ChainPem != null && c.EncryptedPrivateKey != null)
            .Select(c => new { c.Hostname, c.ChainPem, c.EncryptedPrivateKey })
            .ToListAsync(cancellationToken);

        Dictionary<string, EdgeZoneTlsConfig> map = new(active.Count);
        foreach(var cert in active) {
            try {
                map[cert.Hostname] = new EdgeZoneTlsConfig(
                    cert.ChainPem!, protector.Decrypt(cert.EncryptedPrivateKey!));
            }
            catch(Exception ex) {
                // One undecryptable key (rotated EncryptionKey?) must not
                // block snapshot distribution for the other zones.
                logger.LogWarning("Private key for {Hostname} could not be decrypted: {Error}",
                    cert.Hostname, ex.Message);
            }
        }

        return map;
    }
}
