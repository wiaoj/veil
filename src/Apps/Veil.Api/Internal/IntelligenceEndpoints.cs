using IHttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Veil.Api.Internal;

/// <summary>
/// Dashboard-facing proxy for the AI traffic-analysis incidents. The incident
/// store is process-local to the analytics worker, so the control plane forwards
/// to the worker's <c>/intelligence/incidents</c> endpoint. Sits behind the
/// normal user-session auth (no <c>AllowAnonymous</c>), unlike the worker
/// endpoint which is currently open on its internal port.
/// </summary>
public static class IntelligenceEndpoints {
    public const string HttpClientName = "intelligence-proxy";

    public static void Map(WebApplication app) {
        app.MapGet("/v1/intelligence/incidents", Handle)
           .WithName("GetIntelligenceIncidents");
    }

    private static async Task<IHttpResult> Handle(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        int? limit,
        CancellationToken cancellationToken) {

        string workerUrl = configuration.GetSection("Intelligence")["WorkerUrl"] ?? "http://localhost:5001";
        string url = $"{workerUrl.TrimEnd('/')}/intelligence/incidents?limit={Math.Clamp(limit ?? 50, 1, 200)}";

        try {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
            if(!response.IsSuccessStatusCode)
                return Results.Json(Array.Empty<object>());   // worker down/disabled → empty feed

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Results.Content(json, "application/json");
        }
        catch(Exception) {
            // The worker may be down or intelligence disabled — degrade to empty.
            return Results.Json(Array.Empty<object>());
        }
    }
}
