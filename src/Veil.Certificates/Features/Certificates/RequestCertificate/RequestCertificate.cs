using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Certificates.Domain;
using Veil.Certificates.Domain.Enums;
using Veil.Certificates.Domain.ValueObjects;
using Veil.Certificates.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.Certificates.Features.Certificates.RequestCertificate;

public sealed record RequestCertificateRequest(string Hostname);

public sealed record RequestCertificateResponse(
    string Id,
    string Hostname,
    string Status,
    DateTimeOffset RequestedAtUtc);

public sealed class RequestCertificateEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/certificates", Handle)
           .WithName("RequestCertificate")
           .WithTags("Certificates")
           .WithSummary("Requests a certificate for a hostname")
           .WithDescription("Creates a pending certificate order. The ACME worker provisions it asynchronously.")
           .Produces<RequestCertificateResponse>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IHttpResult> Handle(
        RequestCertificateRequest request,
        IDbContextFactory<CertificatesDbContext> dbFactory,
        IObfuscator<CertificateId> obfuscator,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {

        Result<Certificate> certificate = Certificate.Request(request.Hostname, timeProvider.GetUtcNow());
        if(certificate.IsFailure) return certificate.ToProblemDetails();

        await using CertificatesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        string hostname = certificate.Value.Hostname;
        bool inFlight = await dbContext.Certificates
            .AsNoTracking()
            .AnyAsync(c => c.Hostname == hostname
                && (c.Status == CertificateStatus.Pending || c.Status == CertificateStatus.Active),
                cancellationToken);
        if(inFlight) {
            Result<Certificate> conflict = CertificateErrors.AlreadyRequested(hostname);
            return conflict.ToProblemDetails();
        }

        dbContext.Certificates.Add(certificate.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = new RequestCertificateResponse(
            obfuscator.EncodeId(certificate.Value),
            certificate.Value.Hostname,
            certificate.Value.Status.ToString(),
            certificate.Value.RequestedAtUtc);

        return Results.Created($"/v1/certificates/{response.Id}", response);
    }
}
