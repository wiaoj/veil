using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veil.Shared;
using Veil.Zones.Domain;
using Veil.Zones.Domain.ValueObjects;
using Veil.Zones.Infrastructure.Persistence;
using IHttpResult = Microsoft.AspNetCore.Http.IResult;

namespace Veil.Api.Schemas;

/// <summary>
/// Dashboard-facing schema management. Uploads forward to the registry (Vaultify),
/// which validates + versions them — so a `body_schema` rule only ever references a
/// schema that already parsed and passed compatibility checks, and Veil never needs
/// a JSON-Schema validator of its own. The GET proxies read Vaultify (subjects,
/// versions, raw content, diff) and pre-check compatibility; the usage endpoint is
/// Veil-specific, scanning the control plane for rules that reference a subject.
/// </summary>
public static class SchemaEndpoints {
    public sealed record RegisterSchemaRequest(string Subject, string Version, JsonElement Content);

    public sealed record CheckCompatibilityRequest(JsonElement Content);

    /// <summary>A rule that references a schema subject, with a link back to its zone.</summary>
    public sealed record SchemaUsageItem(
        string ZoneId, string Hostname, string RuleId, string RuleName, string Versions);

    public static void Map(WebApplication app) {
        app.MapPost("/v1/schemas", Register)
           .WithName("RegisterSchema")
           .WithTags("Schemas");

        app.MapGet("/v1/schemas", ListSubjects)
           .WithName("ListSchemaSubjects")
           .WithTags("Schemas");

        app.MapGet("/v1/schemas/{subject}/versions", ListVersions)
           .WithName("ListSchemaVersions")
           .WithTags("Schemas");

        app.MapGet("/v1/schemas/{subject}/versions/{version}", GetRaw)
           .WithName("GetSchemaVersion")
           .WithTags("Schemas");

        app.MapPost("/v1/schemas/{subject}/compatibility", CheckCompatibility)
           .WithName("CheckSchemaCompatibility")
           .WithTags("Schemas");

        app.MapGet("/v1/schemas/{subject}/diff", GetDiff)
           .WithName("DiffSchemaVersions")
           .WithTags("Schemas");

        app.MapGet("/v1/schemas/{subject}/usage", GetUsage)
           .WithName("GetSchemaUsage")
           .WithTags("Schemas");
    }

    private static async Task<IHttpResult> Register(
        RegisterSchemaRequest req,
        ISchemaRegistry registry,
        CancellationToken cancellationToken) {

        if(!registry.IsEnabled)
            return RegistryDisabled();

        if(string.IsNullOrWhiteSpace(req.Subject) || string.IsNullOrWhiteSpace(req.Version))
            return Results.BadRequest(new { error = "subject and version are required" });

        Result<SchemaRef> result = await registry.RegisterAsync(
            req.Subject, req.Version, req.Content, cancellationToken);

        return result.Match(
            onValue: IHttpResult (r) => Results.Ok(new { subject = r.Subject, version = r.Version }),
            onError: errors => Results.UnprocessableEntity(new { error = errors[0].Description }));
    }

    private static async Task<IHttpResult> ListSubjects(
        ISchemaRegistry registry, CancellationToken cancellationToken) {
        if(!registry.IsEnabled)
            return RegistryDisabled();
        return Json(await registry.ListSubjectsAsync(cancellationToken));
    }

    private static async Task<IHttpResult> ListVersions(
        string subject, ISchemaRegistry registry, CancellationToken cancellationToken) {
        if(!registry.IsEnabled)
            return RegistryDisabled();
        return Json(await registry.ListVersionsAsync(subject, cancellationToken));
    }

    private static async Task<IHttpResult> GetRaw(
        string subject, string version, ISchemaRegistry registry, CancellationToken cancellationToken) {
        if(!registry.IsEnabled)
            return RegistryDisabled();
        string? content = await registry.GetRawAsync(subject, version, cancellationToken);
        return content is null
            ? Results.NotFound(new { error = $"schema {subject}@{version} not found" })
            : Json(content);
    }

    private static async Task<IHttpResult> CheckCompatibility(
        string subject,
        CheckCompatibilityRequest req,
        ISchemaRegistry registry,
        CancellationToken cancellationToken) {
        if(!registry.IsEnabled)
            return RegistryDisabled();
        SchemaCompatibilityResult result = await registry.CheckCompatibilityAsync(
            subject, req.Content, cancellationToken);
        return Results.Ok(new { compatible = result.Compatible, detail = result.Detail });
    }

    private static async Task<IHttpResult> GetDiff(
        string subject, string? from, string? to,
        ISchemaRegistry registry, CancellationToken cancellationToken) {
        if(!registry.IsEnabled)
            return RegistryDisabled();
        if(string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return Results.BadRequest(new { error = "from and to query parameters are required" });
        return Json(await registry.GetDiffAsync(subject, from, to, cancellationToken));
    }

    /// <summary>Veil-specific: which zones' rules reference this schema subject.
    /// A local scan of the control plane, so it works regardless of registry state.</summary>
    private static async Task<IHttpResult> GetUsage(
        string subject,
        IDbContextFactory<ZonesDbContext> dbFactory,
        IObfuscator<ZoneId> zoneObfuscator,
        IObfuscator<RuleId> ruleObfuscator,
        CancellationToken cancellationToken) {

        await using ZonesDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        List<Zone> zones = await db.Zones
            .AsNoTracking()
            .Include(z => z.Rules)
            .ToListAsync(cancellationToken);

        List<SchemaUsageItem> items = zones
            .SelectMany(z => z.Rules.Select(r => (Zone: z, Rule: r)))
            .Select(t => (t.Zone, t.Rule, Versions: t.Rule.Conditions
                .OfType<BodySchemaMatchCondition>()
                .Where(c => string.Equals(c.Subject, subject, StringComparison.Ordinal))
                .Select(c => c.Version)
                .Distinct()
                .ToArray()))
            .Where(t => t.Versions.Length > 0)
            .Select(t => new SchemaUsageItem(
                zoneObfuscator.Encode(t.Zone.Id),
                t.Zone.Hostname.Value,
                ruleObfuscator.Encode(t.Rule.Id),
                t.Rule.Name,
                string.Join(", ", t.Versions)))
            .ToList();

        return Results.Ok(new { subject, items });
    }

    /// <summary>Passes raw JSON (already unwrapped from Vaultify's envelope) through
    /// verbatim — avoids a parse/re-serialize round-trip for pure proxy responses.</summary>
    private static IHttpResult Json(string rawJson) =>
        Results.Content(rawJson, "application/json");

    private static IHttpResult RegistryDisabled() =>
        Results.Problem(
            title: "Schema registry disabled",
            detail: "No schema registry is configured (Vaultify:BaseUrl).",
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
