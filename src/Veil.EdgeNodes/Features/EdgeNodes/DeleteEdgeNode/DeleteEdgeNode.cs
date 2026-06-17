using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.ValueObjects;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Endpoints;

namespace Veil.EdgeNodes.Features.EdgeNodes.DeleteEdgeNode;

public sealed class DeleteEdgeNodeEndpoint : IEndpoint {
    public void Map(IEndpointRouteBuilder app) {
        app.MapDelete("/v1/edge-nodes/{id}", Handle)
           .WithName("DeleteEdgeNode")
           .WithTags("EdgeNodes")
           .WithSummary("Deletes an edge node")
           .WithDescription("Removes the node registration. The node can no longer pull or receive config and must be re-registered.")
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

        dbContext.EdgeNodes.Remove(node);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}
