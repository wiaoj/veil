using System.Text.Json;
using Veil.Shared;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Veil.Api.Schemas;

/// <summary>
/// Dashboard-facing schema upload. Registering a schema here forwards it to the
/// registry (Vaultify), which validates + versions it — so a `body_schema` rule
/// only ever references a schema that already parsed and passed compatibility
/// checks, and Veil never needs a JSON-Schema validator of its own.
/// </summary>
public static class SchemaEndpoints {
    public sealed record RegisterSchemaRequest(string Subject, string Version, JsonElement Content);

    public static void Map(WebApplication app) {
        app.MapPost("/v1/schemas", Register)
           .WithName("RegisterSchema")
           .WithTags("Schemas");
    }

    private static async Task<IHttpResult> Register(
        RegisterSchemaRequest req,
        ISchemaRegistry registry,
        CancellationToken cancellationToken) {

        if(!registry.IsEnabled)
            return Results.Problem(
                title: "Schema registry disabled",
                detail: "No schema registry is configured (Vaultify:BaseUrl).",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        if(string.IsNullOrWhiteSpace(req.Subject) || string.IsNullOrWhiteSpace(req.Version))
            return Results.BadRequest(new { error = "subject and version are required" });

        Result<SchemaRef> result = await registry.RegisterAsync(
            req.Subject, req.Version, req.Content, cancellationToken);

        return result.Match(
            onValue: IHttpResult (r) => Results.Ok(new { subject = r.Subject, version = r.Version }),
            onError: errors => Results.UnprocessableEntity(new { error = errors[0].Description }));
    }
}
