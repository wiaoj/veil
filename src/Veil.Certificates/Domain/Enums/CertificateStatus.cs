namespace Veil.Certificates.Domain.Enums;

public enum CertificateStatus {
    /// <summary>Requested; ACME order not yet completed.</summary>
    Pending,
    /// <summary>Issued and within its validity window.</summary>
    Active,
    /// <summary>The last provisioning/renewal attempt failed.</summary>
    Failed,
    /// <summary>Validity window has passed without a successful renewal.</summary>
    Expired,
    /// <summary>Explicitly revoked or superseded.</summary>
    Revoked,
}
