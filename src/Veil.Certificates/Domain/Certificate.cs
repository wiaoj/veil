using Veil.Certificates.Domain.Enums;
using Veil.Certificates.Domain.Events;
using Veil.Certificates.Domain.ValueObjects;
using Wiaoj.Security;

namespace Veil.Certificates.Domain;

/// <summary>
/// TLS certificate lifecycle for a single hostname. The private key is stored
/// encrypted via <see cref="ISecretProtector{PrivateKeySecretContext}"/>; the
/// plaintext key never touches the database. Provisioning is asynchronous: a
/// request starts in <see cref="CertificateStatus.Pending"/> and the ACME
/// worker moves it to Active or Failed.
/// </summary>
public sealed class Certificate : Aggregate<CertificateId> {
    public string Hostname { get; private set; }
    public CertificateStatus Status { get; private set; }
    /// <summary>PEM certificate chain (leaf first). Null until issued.</summary>
    public string? ChainPem { get; private set; }
    /// <summary>Encrypted PEM private key managed by Wiaoj.Security. Null until issued.</summary>
    public EncryptedSecret<PrivateKeySecretContext>? EncryptedPrivateKey { get; private set; }
    /// <summary>Tracks which encryption key version protects the private key.</summary>
    public int EncryptedPrivateKeyVersion { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? IssuedAtUtc { get; private set; }
    public DateTimeOffset? ExpiresAtUtc { get; private set; }
    public string? LastError { get; private set; }

    private Certificate() { }

    public static Result<Certificate> Request(string hostname, DateTimeOffset requestedAtUtc) {
        hostname = hostname?.Trim().ToLowerInvariant() ?? string.Empty;
        if(hostname.Length == 0 || Uri.CheckHostName(hostname) != UriHostNameType.Dns)
            return CertificateErrors.HostnameInvalid(hostname);

        return Result<Certificate>.Success(new Certificate {
            Id = CertificateId.New(),
            Hostname = hostname,
            Status = CertificateStatus.Pending,
            RequestedAtUtc = requestedAtUtc
        });
    }

    /// <summary>Completes a pending order or an in-place renewal.</summary>
    public Result<Success> MarkIssued(
        string chainPem,
        EncryptedSecret<PrivateKeySecretContext> encryptedPrivateKey,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc) {
        if(this.Status is not (CertificateStatus.Pending or CertificateStatus.Active or CertificateStatus.Failed))
            return CertificateErrors.NotPending;

        if(string.IsNullOrWhiteSpace(chainPem))
            return CertificateErrors.MaterialEmpty;

        this.ChainPem = chainPem;
        this.EncryptedPrivateKey = encryptedPrivateKey;
        this.EncryptedPrivateKeyVersion = encryptedPrivateKey.KeyVersion.Value;
        this.IssuedAtUtc = issuedAtUtc;
        this.ExpiresAtUtc = expiresAtUtc;
        this.Status = CertificateStatus.Active;
        this.LastError = null;

        RaiseDomainEvent(new CertificateIssuedDomainEvent(this.Id, this.Hostname));

        return Result.Success();
    }

    public Result<Success> MarkFailed(string error) {
        this.LastError = error;
        if(this.Status is CertificateStatus.Pending)
            this.Status = CertificateStatus.Failed;
        // A failed renewal keeps the still-valid material serving traffic:
        // an Active certificate stays Active, only LastError is recorded.
        return Result.Success();
    }

    public Result<Success> MarkExpired(DateTimeOffset nowUtc) {
        if(this.ExpiresAtUtc is null || this.ExpiresAtUtc > nowUtc)
            return CertificateErrors.NotActive;
        this.Status = CertificateStatus.Expired;
        return Result.Success();
    }

    public Result<Success> Revoke() {
        if(this.Status is not CertificateStatus.Active)
            return CertificateErrors.NotActive;
        this.Status = CertificateStatus.Revoked;
        RaiseDomainEvent(new CertificateRevokedDomainEvent(this.Id, this.Hostname));
        return Result.Success();
    }

    /// <summary>Re-encrypts the private key with the current active encryption key.</summary>
    public void RotatePrivateKey(EncryptedSecret<PrivateKeySecretContext> rotatedKey) {
        this.EncryptedPrivateKey = rotatedKey;
        this.EncryptedPrivateKeyVersion = rotatedKey.KeyVersion.Value;
    }

    /// <summary>True when the certificate should be renewed (within the window before expiry).</summary>
    public bool IsDueForRenewal(DateTimeOffset nowUtc, TimeSpan renewBefore) {
        return this.Status is CertificateStatus.Active
            && this.ExpiresAtUtc is not null
            && this.ExpiresAtUtc - nowUtc <= renewBefore;
    }
}
