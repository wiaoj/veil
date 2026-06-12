using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.Certificates.Domain.ValueObjects;
using Veil.Certificates.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.Certificates.Features.Certificates.ListCertificates;

public sealed record CertificateSummaryResponse(
    string Id,
    string Hostname,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record ListCertificatesResponse(
    List<CertificateSummaryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class ListCertificatesEndpoint : IEndpoint {
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public void Map(IEndpointRouteBuilder app) {
        app.MapGet("/v1/certificates", Handle)
           .WithName("ListCertificates")
           .WithTags("Certificates")
           .WithSummary("Lists certificates")
           .WithDescription("Returns a paginated list of certificates ordered by request time (snowflake id).")
           .Produces<ListCertificatesResponse>(StatusCodes.Status200OK);
    }

    private static async Task<IHttpResult> Handle(
        IDbContextFactory<CertificatesDbContext> dbFactory,
        IObfuscator<CertificateId> obfuscator,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = DefaultPageSize) {

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        await using CertificatesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        int totalCount = await dbContext.Certificates.CountAsync(cancellationToken);

        var certificates = await dbContext.Certificates
            .AsNoTracking()
            .OrderByDescending(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new {
                c.Id,
                c.Hostname,
                c.Status,
                c.RequestedAtUtc,
                c.ExpiresAtUtc
            })
            .ToListAsync(cancellationToken);

        List<CertificateSummaryResponse> items = certificates
            .Select(c => new CertificateSummaryResponse(
                obfuscator.Encode(c.Id),
                c.Hostname,
                c.Status.ToString(),
                c.RequestedAtUtc,
                c.ExpiresAtUtc))
            .ToList();

        return Results.Ok(new ListCertificatesResponse(items, page, pageSize, totalCount));
    }
}
