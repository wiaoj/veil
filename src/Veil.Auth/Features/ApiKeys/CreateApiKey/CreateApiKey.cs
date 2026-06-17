using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Veil.Auth.Audit;
using Veil.Auth.Domain;
using Veil.Auth.Domain.ValueObjects;
using Veil.Auth.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;
using Wiaoj.Primitives.Collections;
using Wiaoj.Primitives.Cryptography.Hashing;

namespace Veil.Auth.Features.ApiKeys.CreateApiKey;

public sealed record CreateApiKeyRequest(string Name, List<string>? Scopes = null);

/// <summary>
/// The created key. <paramref name="Key"/> is returned exactly once — only
/// its SHA-256 hash is stored.
/// </summary>
public sealed record CreateApiKeyResponse(
    string Id,
    string Name,
    IEnumerable<string> Scopes,
    string Key,
    DateTimeOffset CreatedAtUtc);

public sealed class CreateApiKeyEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/api-keys", Handle)
           .WithName("CreateApiKey")
           .WithTags("ApiKeys")
           .WithSummary("Creates a new API key")
           .WithDescription("Issues a machine credential for the management API. The plaintext key is shown only once. Requires a user session (not another API key).")
           .Produces<CreateApiKeyResponse>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .Produces(StatusCodes.Status403Forbidden);
    }

    private static async Task<IHttpResult> Handle(
        CreateApiKeyRequest req,
        ClaimsPrincipal caller,
        IDbContextFactory<AuthDbContext> dbFactory,
        IObfuscator<UserId> userObfuscator,
        IObfuscator<ApiKeyId> keyObfuscator,
        IAuditLogger audit,
        HttpContext httpContext,
        CancellationToken cancellationToken) {

        // Keys are minted by humans: the creator comes from the JWT subject.
        // An API key principal has no subject and cannot create more keys.
        string? subject = caller.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? caller.FindFirstValue("sub");

        if(subject is null || !userObfuscator.TryDecode(subject, out UserId createdBy))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        using Secret<byte> rawBytes = Secret<byte>.Generate(32);

        string plaintextKey = rawBytes.Expose(bytes => $"vak_{HexString.FromBytesLower(bytes)}");

        HexString keyHash = Sha256Hash.Compute(plaintextKey).ToHexStringLower();

        Result<ApiKey> apiKey = ApiKey.Create(
            req.Name, keyHash, req.Scopes ?? [], createdBy);

        if(apiKey.IsFailure)
            return apiKey.ToProblemDetails();

        await using AuthDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.ApiKeys.AddAsync(apiKey.Value, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        string encodedId = keyObfuscator.EncodeId(apiKey.Value);

        await audit.WriteAsync(
            AuditActions.ApiKeyCreated,
            AuditOutcome.Success,
            actor: subject,
            actorIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            target: encodedId,
            detail: apiKey.Value.Name,
            cancellationToken: cancellationToken);

        return Results.Created($"/v1/api-keys/{encodedId}", new CreateApiKeyResponse(
            encodedId,
            apiKey.Value.Name,
            apiKey.Value.Scopes,
            plaintextKey,
            apiKey.Value.CreatedAt));
    }
}