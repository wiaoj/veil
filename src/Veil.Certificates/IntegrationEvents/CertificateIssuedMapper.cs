using Veil.Certificates.Domain.Events;
using Veil.Certificates.Domain.ValueObjects;
using Veil.Shared;

namespace Veil.Certificates.IntegrationEvents;

/// <summary>
/// Domain event → integration event mapper, picked up by the module's
/// AddTytoIntegration scan and auto-published post-commit.
/// </summary>
public sealed class CertificateIssuedMapper(IObfuscator<CertificateId> obfuscator)
    : IIntegrationEventMapper<CertificateIssuedDomainEvent, CertificateIssued> {
    public CertificateIssued Map(CertificateIssuedDomainEvent @event) {
        return new CertificateIssued(obfuscator.Encode(@event.CertificateId), @event.Hostname, @event.OccurredAt);
    }
}
