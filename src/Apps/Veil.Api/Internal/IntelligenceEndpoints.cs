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

        // Manual one-click apply: the dashboard sends an incident's suggested rule
        // (zone + condition/action + shadow flag) and the control plane creates it
        // in-process via the same path the worker's auto-apply uses. Behind the
        // normal user-session auth (a privileged mutation).
        app.MapPost("/v1/intelligence/incidents/apply", Apply)
           .WithName("ApplyIntelligenceRule");
    }

    private static async Task<IHttpResult> Apply(
        AiRuleService service,
        ApplyAiRuleRequest request,
        CancellationToken cancellationToken) {

        if(string.IsNullOrWhiteSpace(request.Zone) || request.Rule is null)
            return Results.BadRequest(new { error = "zone and rule are required" });

        ApplyAiRuleResult result =
            await service.ApplyAsync(request.Zone, request.Rule, request.Shadow, cancellationToken);

        // Always 200 with the outcome — "applied" or a human-readable reason it
        // wasn't (zone not found, unmappable condition) — so the dashboard can
        // surface the reason instead of a generic error.
        return Results.Ok(result);
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
