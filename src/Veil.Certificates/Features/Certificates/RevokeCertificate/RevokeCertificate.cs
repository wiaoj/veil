using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Certificates.Domain;
using Veil.Certificates.Domain.ValueObjects;
using Veil.Certificates.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.Certificates.Features.Certificates.RevokeCertificate;

public sealed class RevokeCertificateEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/certificates/{id}/revoke", Handle)
           .WithName("RevokeCertificate")
           .WithTags("Certificates")
           .WithSummary("Revokes an active certificate")
           .WithDescription("Marks an active certificate as revoked. Only active certificates can be revoked.")
           .Produces(StatusCodes.Status204NoContent)
           .ProducesProblem(StatusCodes.Status400BadRequest)
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
            .FirstOrDefaultAsync(c => c.Id.Equals(certId.Value), cancellationToken);

        if(certificate is null) {
            Result<Certificate> notFound = CertificateErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        Result<Success> result = certificate.Revoke();
        if(result.IsFailure) return result.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}
