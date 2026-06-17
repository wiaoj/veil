using Tyto;

namespace Veil.Certificates.IntegrationEvents;

/// <summary>
/// Published on the bus when a certificate is revoked. ConfigSync handles it
/// to re-push edge config so nodes stop serving the revoked material.
/// </summary>
[Message("certificates.revoked", 1)]
public sealed record CertificateRevoked(string CertificateId, string Hostname, DateTimeOffset OccurredAtUtc) : IEvent;
