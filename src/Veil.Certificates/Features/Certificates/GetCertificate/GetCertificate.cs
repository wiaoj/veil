using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Certificates.Domain;
using Veil.Certificates.Domain.ValueObjects;
using Veil.Certificates.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.Certificates.Features.Certificates.GetCertificate;

/// <summary>
/// Certificate detail. Key material is never exposed — only lifecycle
/// metadata leaves the module through the public API.
/// </summary>
public sealed record GetCertificateResponse(
    string Id,
    string Hostname,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? IssuedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? LastError);

public sealed class GetCertificateEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/certificates/{id}", Handle)
           .WithName("GetCertificate")
           .WithTags("Certificates")
           .WithSummary("Gets a certificate by id")
           .Produces<GetCertificateResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        IDbContextFactory<CertificatesDbContext> dbFactory,
        IObfuscator<CertificateId> obfuscator,
        CancellationToken cancellationToken) {

        Result<CertificateId> certId = obfuscator.Decode(id);
        if(certId.IsFailure) return certId.ToProblemDetails();

        await using CertificatesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        Certificate? certificate = await dbContext.Certificates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id.Equals(certId.Value), cancellationToken);

        if(certificate is null) {
            Result<Certificate> notFound = CertificateErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        return Results.Ok(new GetCertificateResponse(
            obfuscator.EncodeId(certificate),
            certificate.Hostname,
            certificate.Status.ToString(),
            certificate.RequestedAtUtc,
            certificate.IssuedAtUtc,
            certificate.ExpiresAtUtc,
            certificate.LastError));
    }
}
