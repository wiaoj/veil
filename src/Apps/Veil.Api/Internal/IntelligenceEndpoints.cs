using Tyto.Rpc;
using Veil.Analytics.Intelligence;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Veil.Api.Internal;

/// <summary>
/// Dashboard-facing feed for the AI traffic-analysis incidents. The incident
/// store is process-local to the analytics worker, so the control plane fetches
/// it over Tyto RPC-over-HTTP (Phase 12, <see cref="GetIncidentsRequest"/>) —
/// replacing the previous bespoke <see cref="System.Net.Http.HttpClient"/> proxy.
/// Sits behind the normal user-session auth (no <c>AllowAnonymous</c>). Degrades
/// to an empty feed when the worker is down or intelligence is disabled.
/// </summary>
public static class IntelligenceEndpoints {
    public static void Map(WebApplication app) {
        app.MapGet("/v1/intelligence/incidents", Handle)
           .WithName("GetIntelligenceIncidents");
    }

    private static async Task<IHttpResult> Handle(
        IRpcClient rpcClient,
        int? limit,
        CancellationToken cancellationToken) {

        GetIncidentsRequest request = new(Math.Clamp(limit ?? 50, 1, 200));

        RpcResult<TrafficIncident[]> result =
            await rpcClient.CallAsync<GetIncidentsRequest, TrafficIncident[]>(request, cancellationToken);

        // Worker down / intelligence disabled → degrade to an empty feed rather
        // than surfacing the RPC error to the dashboard.
        return result.Match(
            onSuccess: IHttpResult (incidents) => Results.Json(incidents),
            onError: _ => Results.Json(Array.Empty<TrafficIncident>()));
    }
}
