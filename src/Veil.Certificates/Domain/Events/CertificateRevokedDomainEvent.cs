using Veil.Certificates.Domain.ValueObjects;

namespace Veil.Certificates.Domain.Events;

/// <summary>
/// Raised when an active certificate is revoked. The config-sync pipeline
/// reacts by re-pushing edge config so nodes stop serving the material.
/// </summary>
public sealed record CertificateRevokedDomainEvent(CertificateId CertificateId, string Hostname) : DomainEvent;
