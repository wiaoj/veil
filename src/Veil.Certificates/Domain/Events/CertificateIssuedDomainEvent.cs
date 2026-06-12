using Veil.Certificates.Domain.ValueObjects;

namespace Veil.Certificates.Domain.Events;

/// <summary>
/// Raised when a certificate becomes Active (first issuance or renewal).
/// The config-sync pipeline reacts by pushing the new material to edge nodes.
/// </summary>
public sealed record CertificateIssuedDomainEvent(CertificateId CertificateId, string Hostname) : DomainEvent;
