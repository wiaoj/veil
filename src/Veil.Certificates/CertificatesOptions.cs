namespace Veil.Certificates;

/// <summary>
/// Certificate lifecycle configuration (<c>Certificates</c> section).
/// </summary>
public sealed class CertificatesOptions {
    public const string SectionName = "Certificates";

    /// <summary>
    /// AES-256-GCM key protecting private keys at rest, 64 hex chars.
    /// Unset → the ACME worker is disabled (keys must never be stored
    /// plaintext).
    /// </summary>
    public string? EncryptionKey { get; init; }

    /// <summary>
    /// ACME v2 directory URL. Unset → the ACME worker is disabled.
    /// Let's Encrypt staging: https://acme-staging-v02.api.letsencrypt.org/directory
    /// </summary>
    public string? AcmeDirectoryUrl { get; init; }

    /// <summary>Contact e-mail registered with the ACME account.</summary>
    public string? AcmeAccountEmail { get; init; }

    /// <summary>Renew certificates expiring within this many days.</summary>
    public int RenewBeforeDays { get; init; } = 30;

    /// <summary>Maximum time to wait for an ACME order to become valid.</summary>
    public int OrderTimeoutSeconds { get; init; } = 60;

    /// <summary>Reserved edge path receiving the HTTP-01 challenge set.</summary>
    public string ChallengePushPath { get; init; } = "/_veil/internal/acme-challenge";

    /// <summary>
    /// Accept the ACME directory's TLS certificate without validation.
    /// Development only — Pebble and other ACME test servers use self-signed
    /// certificates.
    /// </summary>
    public bool AcmeAllowUntrustedTls { get; init; }
}
