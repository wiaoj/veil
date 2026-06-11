using Veil.Analytics.Contracts;
using Veil.Analytics.Ingestion;
using Veil.EdgeNodes.Contracts;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Veil.Analytics.Worker.Internal;

/// <summary>
/// Edge → control plane log ingestion. Lives in the host (not a module)
/// because it composes two modules: EdgeNodes authenticates the caller via
/// <see cref="IEdgeNodeTokenVerifier"/>, Analytics owns the queue.
/// </summary>
public static class IngestEndpoints {
    public const string NodeTokenHeader = "X-Veil-Node-Token";

    public static void Map(WebApplication app) {
        app.MapPost("/ingest", Handle)
           .WithName("IngestRequestLogs")
           .ExcludeFromDescription();
    }

    private static async Task<IHttpResult> Handle(
        IngestRequest payload,
        HttpRequest request,
        IEdgeNodeTokenVerifier tokenVerifier,
        RequestLogQueue queue,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) {

        string? token = request.Headers[NodeTokenHeader].FirstOrDefault();
        if(string.IsNullOrEmpty(token))
            return Results.Unauthorized();

        switch(await tokenVerifier.VerifyAsync(payload.NodeId, token, cancellationToken)) {
            case EdgeNodeTokenVerdict.Invalid:
                return Results.Unauthorized();
            case EdgeNodeTokenVerdict.Disabled:
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if(payload.Records is not { Count: > 0 })
            return Results.Accepted(value: new IngestResponse(0));

        List<RequestLogRow> rows = IngestNormalizer.Normalize(
            payload.NodeId, payload.Records, timeProvider.GetUtcNow());
        queue.Enqueue(rows);

        return Results.Accepted(value: new IngestResponse(rows.Count));
    }
}

public sealed record IngestResponse(int Accepted);
