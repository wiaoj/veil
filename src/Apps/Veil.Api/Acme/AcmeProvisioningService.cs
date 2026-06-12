using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;
using Veil.Certificates;
using Veil.Certificates.Domain;
using Veil.Certificates.Domain.Enums;
using Veil.Certificates.Infrastructure.Persistence;
using Veil.Certificates.Infrastructure.Security;

namespace Veil.Api.Acme;

/// <summary>
/// Provisions pending certificates and renews expiring ones via ACME v2
/// (HTTP-01). The challenge token is published to every enabled edge node
/// (any node behind the hostname can answer), the order is polled up to
/// <see cref="CertificatesOptions.OrderTimeoutSeconds"/>, and the private
/// key is AES-256-GCM encrypted before it touches the database.
///
/// Requires <c>Certificates:AcmeDirectoryUrl</c> and
/// <c>Certificates:EncryptionKey</c>; unset → the worker idles. The ACME
/// account is created per service lifetime (account key persistence comes
/// with multi-instance support).
/// </summary>
public sealed class AcmeProvisioningService(
    IDbContextFactory<CertificatesDbContext> certsDbFactory,
    EdgeChallengePublisher challengePublisher,
    TimeProvider timeProvider,
    IOptions<CertificatesOptions> options,
    ILogger<AcmeProvisioningService> logger) : BackgroundService {

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OrderPollDelay = TimeSpan.FromSeconds(2);

    private readonly CertificatesOptions _options = options.Value;
    private IAccountContext? _account;
    private AcmeContext? _acme;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        PrivateKeyProtector? protector = PrivateKeyProtector.FromHex(this._options.EncryptionKey);
        if(this._options.AcmeDirectoryUrl is null || protector is null) {
            logger.LogInformation(
                "ACME worker disabled: Certificates:AcmeDirectoryUrl and Certificates:EncryptionKey (64 hex) are required");
            return;
        }

        logger.LogInformation("ACME worker started (directory {Directory}, renew within {Days} days)",
            this._options.AcmeDirectoryUrl, this._options.RenewBeforeDays);

        while(!stoppingToken.IsCancellationRequested) {
            try {
                await ProcessDueCertificatesAsync(protector, stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {
                break;
            }
            catch(Exception ex) {
                logger.LogError(ex, "ACME provisioning cycle failed");
            }

            try {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch(OperationCanceledException) {
                break;
            }
        }
    }

    private async Task ProcessDueCertificatesAsync(PrivateKeyProtector protector, CancellationToken cancellationToken) {
        await using CertificatesDbContext db = await certsDbFactory.CreateDbContextAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset renewCutoff = now.AddDays(this._options.RenewBeforeDays);

        List<Certificate> due = await db.Certificates
            .Where(c => c.Status == CertificateStatus.Pending
                || (c.Status == CertificateStatus.Active && c.ExpiresAtUtc != null && c.ExpiresAtUtc <= renewCutoff))
            .ToListAsync(cancellationToken);

        if(due.Count == 0) return;

        foreach(Certificate certificate in due) {
            try {
                await ProvisionAsync(certificate, protector, cancellationToken);
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
                throw;
            }
            catch(Exception ex) {
                logger.LogWarning(ex, "ACME order for {Hostname} failed", certificate.Hostname);
                certificate.MarkFailed(Truncate(ex.Message, 2048));
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // Validation is done either way — drop the published challenge set.
        await challengePublisher.ClearAsync(cancellationToken);
    }

    private async Task ProvisionAsync(
        Certificate certificate,
        PrivateKeyProtector protector,
        CancellationToken cancellationToken) {
        IAccountContext account = await GetAccountAsync();
        AcmeContext acme = this._acme!;

        IOrderContext order = await acme.NewOrder([certificate.Hostname]);
        IAuthorizationContext authorization = (await order.Authorizations()).First();
        IChallengeContext http = await authorization.Http();

        bool published = await challengePublisher.PublishAsync(
            [new EdgeChallengePublisher.ChallengeEntry(http.Token, http.KeyAuthz)],
            cancellationToken);
        if(!published) {
            certificate.MarkFailed("Challenge hiçbir edge node'a yayınlanamadı.");
            return;
        }

        await http.Validate();

        // Poll until the order leaves 'pending'/'processing' or the budget runs out.
        DateTimeOffset deadline = timeProvider.GetUtcNow().AddSeconds(this._options.OrderTimeoutSeconds);
        Order resource = await order.Resource();
        while(resource.Status is OrderStatus.Pending or OrderStatus.Processing
            && timeProvider.GetUtcNow() < deadline) {
            await Task.Delay(OrderPollDelay, cancellationToken);
            resource = await order.Resource();
        }

        if(resource.Status is not (OrderStatus.Ready or OrderStatus.Valid)) {
            Authorization? authz = await authorization.Resource();
            string? detail = authz?.Challenges?
                .Select(c => c.Error?.Detail)
                .FirstOrDefault(d => d is not null);
            certificate.MarkFailed(Truncate(
                $"ACME order durumu: {resource.Status}. {detail}".Trim(), 2048));
            return;
        }

        IKey privateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        CertificateChain chain = await GenerateWithNonceRetryAsync(order, certificate.Hostname, privateKey);

        // chain.ToPem() resolves issuers against Certes' embedded CA store,
        // which doesn't know test CAs (Pebble). The order response already
        // carries the full chain — concatenate it directly.
        string chainPem = string.Join('\n',
            new[] { chain.Certificate.ToPem() }.Concat(chain.Issuers.Select(i => i.ToPem())));
        string encryptedKey = protector.Encrypt(privateKey.ToPem());

        using X509Certificate2 leaf = X509Certificate2.CreateFromPem(chainPem);
        Result<Success> issued = certificate.MarkIssued(
            chainPem, encryptedKey, timeProvider.GetUtcNow(), new DateTimeOffset(leaf.NotAfter.ToUniversalTime()));
        if(issued.IsFailure) {
            logger.LogWarning("Certificate {Hostname} could not be marked issued", certificate.Hostname);
            return;
        }

        logger.LogInformation("Certificate issued for {Hostname}, expires {Expires}",
            certificate.Hostname, leaf.NotAfter.ToUniversalTime());
    }

    /// <summary>
    /// Finalizes the order. <c>badNonce</c> is retryable per RFC 8555 §6.5
    /// (and Pebble rejects a percentage of valid nonces by design); Certes
    /// does not retry it on finalize, so we do.
    /// </summary>
    private static async Task<CertificateChain> GenerateWithNonceRetryAsync(
        IOrderContext order,
        string hostname,
        IKey privateKey) {
        const int attempts = 3;
        for(int attempt = 1; ; attempt++) {
            try {
                return await order.Generate(new CsrInfo { CommonName = hostname }, privateKey);
            }
            catch(AcmeRequestException ex) when(
                attempt < attempts && ex.Error?.Type?.Contains("badNonce") == true) {
            }
        }
    }

    private async Task<IAccountContext> GetAccountAsync() {
        if(this._account is not null) return this._account;

        Uri directory = new(this._options.AcmeDirectoryUrl!);
        if(this._options.AcmeAllowUntrustedTls) {
            // Pebble & friends use self-signed TLS; never enable in production.
            HttpClient insecure = new(new HttpClientHandler {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });
            this._acme = new AcmeContext(directory, http: new AcmeHttpClient(directory, insecure));
        }
        else {
            this._acme = new AcmeContext(directory);
        }

        this._account = await this._acme.NewAccount(
            this._options.AcmeAccountEmail ?? "admin@veil.local", termsOfServiceAgreed: true);
        return this._account;
    }

    private static string Truncate(string value, int max) {
        return value.Length > max ? value[..max] : value;
    }
}
