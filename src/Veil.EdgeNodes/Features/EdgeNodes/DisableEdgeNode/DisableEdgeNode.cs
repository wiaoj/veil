using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.ValueObjects;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.EdgeNodes.Features.EdgeNodes.DisableEdgeNode;

public sealed class DisableEdgeNodeEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapPost("/v1/edge-nodes/{id}/disable", Handle)
           .WithName("DisableEdgeNode")
           .WithTags("EdgeNodes")
           .WithSummary("Disables an edge node")
           .WithDescription("A disabled node no longer receives config pushes.")
           .Produces(StatusCodes.Status204NoContent)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IHttpResult> Handle(
        string id,
        IDbContextFactory<EdgeNodesDbContext> dbFactory,
        IObfuscator<EdgeNodeId> obfuscator,
        CancellationToken cancellationToken) {

        Result<EdgeNodeId> nodeId = obfuscator.Decode(id);
        if(nodeId.IsFailure) return nodeId.ToProblemDetails();

        await using EdgeNodesDbContext dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

        EdgeNode? node = await dbContext.EdgeNodes
            .FirstOrDefaultAsync(n => n.Id.Equals(nodeId.Value), cancellationToken);

        if(node is null) {
            Result<EdgeNode> notFound = EdgeNodeErrors.NotFound;
            return notFound.ToProblemDetails();
        }

        Result<Success> result = node.Disable();
        if(result.IsFailure) return result.ToProblemDetails();

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}
