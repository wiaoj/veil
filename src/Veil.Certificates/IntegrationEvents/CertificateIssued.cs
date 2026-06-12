using Tyto;

namespace Veil.Certificates.IntegrationEvents;

/// <summary>
/// Published on the bus when a certificate becomes Active (first issuance or
/// renewal). ConfigSync handles it to push the new TLS material to edge
/// nodes immediately.
/// </summary>
[Message("certificates.issued", 1)]
public sealed record CertificateIssued(string CertificateId, string Hostname, DateTimeOffset OccurredAtUtc) : IEvent;
