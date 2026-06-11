using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.ValueObjects;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.EdgeNodes.Features.EdgeNodes.RegisterEdgeNode;

/// <summary>
/// The request payload for registering an edge node.
/// </summary>
/// <param name="Name">Human-readable node name (e.g. "fra-1").</param>
/// <param name="Address">Absolute http(s) URL where the node receives config pushes.</param>
public sealed record RegisterEdgeNodeRequest(string Name, string Address);

/// <summary>
/// The response returned upon successful registration.
/// </summary>
/// <param name="Id">The unique identifier of the node.</param>
/// <param name="Name">The registered node name.</param>
/// <param name="Address">The config push address.</param>
/// <param name="Status">Current node status.</param>
/// <param name="Token">The node's authentication token. Shown only once —
/// only its hash is stored. Configure it on the edge node as VEIL_NODE_TOKEN.</param>
public sealed record RegisterEdgeNodeResponse(
    string Id,
    string Name,
    string Address,
    string Status,
    string Token);

public sealed class RegisterEdgeNodeEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/edge-nodes", Handle)
           .WithName("RegisterEdgeNode")
           .WithTags("EdgeNodes")
           .WithSummary("Registers a new edge node")
           .WithDescription("Registers an edge node and issues its authentication token. The token is returned exactly once; only its SHA-256 hash is stored.")
           .Produces<RegisterEdgeNodeResponse>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<IHttpResult> Handle(
        RegisterEdgeNodeRequest req,
        IDbContextFactory<EdgeNodesDbContext> dbFactory,
        IObfuscator<EdgeNodeId> obfuscator,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {

        if(!Uri.TryCreate(req.Address, UriKind.Absolute, out Uri? address)) {
            Result<EdgeNode> invalid = EdgeNodeErrors.AddressInvalid(req.Address);
            return invalid.ToProblemDetails();
        }

        // The plaintext token leaves the control plane exactly once, in this
        // response. Only the SHA-256 hash is persisted.
        string token = $"vnt_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}";
        string tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        Result<EdgeNode> node = EdgeNode.Register(req.Name, address, tokenHash, timeProvider.GetUtcNow());
        if(node.IsFailure) return node.ToProblemDetails();

        await using EdgeNodesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.EdgeNodes.AddAsync(node.Value, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        ObfuscatedId encodedId = obfuscator.EncodeId(node.Value);
        var response = new RegisterEdgeNodeResponse(
            encodedId,
            node.Value.Name,
            node.Value.Address.ToString(),
            node.Value.Status.ToString(),
            token);

        return Results.Created($"/v1/edge-nodes/{encodedId}", response);
    }
}
