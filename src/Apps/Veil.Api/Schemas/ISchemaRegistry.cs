using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Veil.Shared;

namespace Veil.Api.Schemas;

/// <summary>Reference to a schema stored in the registry.</summary>
public sealed record SchemaRef(string Subject, string Version);

/// <summary>Outcome of a compatibility pre-check. <see cref="Compatible"/> is
/// <see langword="null"/> when the registry could not be reached (the caller then
/// proceeds without blocking); <see cref="Detail"/> carries the registry's message
/// (e.g. why a candidate is incompatible), when it supplies one.</summary>
public sealed record SchemaCompatibilityResult(bool? Compatible, string? Detail);

/// <summary>
/// Veil's view of the schema store (Vaultify). Uploads register + validate a
/// schema there (so Veil never has to embed a JSON-Schema validator of its own);
/// resolution fetches the concrete schema when a config snapshot is built.
/// </summary>
public interface ISchemaRegistry {
    /// <summary>True when a registry is configured; when false the schema-reference
    /// feature is off and rules using it are dropped fail-safe.</summary>
    bool IsEnabled { get; }

    /// <summary>Registers (and validates) a schema. The registry rejects invalid
    /// schemas, so a bad upload fails here rather than at the edge.</summary>
    Task<Result<SchemaRef>> RegisterAsync(string subject, string version, JsonElement content, CancellationToken cancellationToken);

    /// <summary>The raw schema JSON for a reference, or null when it cannot be
    /// resolved (missing / registry down) — the caller then omits the rule.</summary>
    Task<string?> ResolveRawAsync(SchemaRef reference, CancellationToken cancellationToken);

    /// <summary>Lists schema subjects as a raw JSON array (Vaultify's HATEOAS
    /// envelope unwrapped to its <c>data</c> node). Returns <c>"[]"</c> when the
    /// registry is unreachable, so the dashboard degrades to an empty list.</summary>
    Task<string> ListSubjectsAsync(CancellationToken cancellationToken);

    /// <summary>Lists a subject's versions as a raw JSON array (envelope
    /// unwrapped); <c>"[]"</c> when unreachable.</summary>
    Task<string> ListVersionsAsync(string subject, CancellationToken cancellationToken);

    /// <summary>Raw schema content for a <paramref name="subject"/>@<paramref name="version"/>,
    /// or null when missing / the registry is down.</summary>
    Task<string?> GetRawAsync(string subject, string version, CancellationToken cancellationToken);

    /// <summary>Pre-checks a candidate schema against a subject's compatibility
    /// rules. <see cref="SchemaCompatibilityResult.Compatible"/> is null when the
    /// check could not be performed (registry unreachable) so the caller doesn't
    /// block the upload on it.</summary>
    Task<SchemaCompatibilityResult> CheckCompatibilityAsync(string subject, JsonElement content, CancellationToken cancellationToken);

    /// <summary>Diff between two versions of a subject as a raw JSON object
    /// (envelope unwrapped); <c>"{}"</c> when unreachable.</summary>
    Task<string> GetDiffAsync(string subject, string from, string to, CancellationToken cancellationToken);
}

/// <summary>No-op registry used when no Vaultify URL is configured.</summary>
public sealed class DisabledSchemaRegistry : ISchemaRegistry {
    public bool IsEnabled => false;

    public Task<Result<SchemaRef>> RegisterAsync(string subject, string version, JsonElement content, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<SchemaRef>(
            Error.Validation("Schema.RegistryDisabled", "No schema registry is configured (Vaultify:BaseUrl).")));

    public Task<string?> ResolveRawAsync(SchemaRef reference, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<string> ListSubjectsAsync(CancellationToken cancellationToken) =>
        Task.FromResult("[]");

    public Task<string> ListVersionsAsync(string subject, CancellationToken cancellationToken) =>
        Task.FromResult("[]");

    public Task<string?> GetRawAsync(string subject, string version, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<SchemaCompatibilityResult> CheckCompatibilityAsync(
        string subject, JsonElement content, CancellationToken cancellationToken) =>
        Task.FromResult(new SchemaCompatibilityResult(null, null));

    public Task<string> GetDiffAsync(string subject, string from, string to, CancellationToken cancellationToken) =>
        Task.FromResult("{}");
}

/// <summary>
/// Vaultify-backed schema registry. Talks to the sibling schema-registry service
/// over HTTP: <c>POST /api/v1/namespaces/{ns}/schemas</c> to register (Vaultify
/// validates + SemVer-versions + compatibility-checks), and
/// <c>GET …/{subject}/versions/{version}/raw</c> to resolve.
/// </summary>
public sealed class VaultifySchemaRegistry(
    IHttpClientFactory httpClientFactory,
    IOptions<VaultifyOptions> options,
    ILogger<VaultifySchemaRegistry> logger) : ISchemaRegistry {

    public const string HttpClientName = "vaultify";

    private readonly VaultifyOptions _options = options.Value;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(this._options.BaseUrl);

    private string Base => this._options.BaseUrl!.TrimEnd('/');
    private string Ns => Uri.EscapeDataString(this._options.Namespace);

    public async Task<Result<SchemaRef>> RegisterAsync(
        string subject, string version, JsonElement content, CancellationToken cancellationToken) {

        var body = new {
            subject,
            version,
            type = "Json",
            content,
        };

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using StringContent payload = new(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            $"{Base}/api/v1/namespaces/{Ns}/schemas", payload, cancellationToken);

        if(response.IsSuccessStatusCode)
            return Result<SchemaRef>.Success(new SchemaRef(subject, version));

        // Surface Vaultify's own validation/compatibility error to the caller.
        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning("Vaultify rejected schema {Subject}@{Version}: {Status} {Detail}",
            subject, version, (int)response.StatusCode, Truncate(detail));
        return Result.Failure<SchemaRef>(Error.Validation(
            "Schema.RegistrationRejected",
            $"Schema registry rejected the schema ({(int)response.StatusCode})."));
    }

    public async Task<string?> ResolveRawAsync(SchemaRef reference, CancellationToken cancellationToken) {
        try {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            string url = $"{Base}/api/v1/namespaces/{Ns}/schemas/" +
                $"{Uri.EscapeDataString(reference.Subject)}/versions/{Uri.EscapeDataString(reference.Version)}/raw";

            using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
            if(!response.IsSuccessStatusCode) {
                logger.LogWarning("Resolving schema {Subject}@{Version} returned {Status}",
                    reference.Subject, reference.Version, (int)response.StatusCode);
                return null;
            }

            // Response is VaultifyResource<RawSchemaResponse> — dig out .data.content.
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            JsonElement root = doc.RootElement;
            JsonElement dataEl = root.TryGetProperty("data", out JsonElement d) ? d : root;
            if(!dataEl.TryGetProperty("content", out JsonElement content))
                return null;

            // content may be a JSON object or a JSON-encoded string.
            return content.ValueKind == JsonValueKind.String ? content.GetString() : content.GetRawText();
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch(Exception ex) {
            logger.LogWarning(ex, "Resolving schema {Subject}@{Version} failed",
                reference.Subject, reference.Version);
            return null;
        }
    }

    public Task<string> ListSubjectsAsync(CancellationToken cancellationToken) =>
        GetUnwrappedAsync($"{Base}/api/v1/namespaces/{Ns}/schemas", "[]", cancellationToken);

    public Task<string> ListVersionsAsync(string subject, CancellationToken cancellationToken) =>
        GetUnwrappedAsync(
            $"{Base}/api/v1/namespaces/{Ns}/schemas/{Uri.EscapeDataString(subject)}/versions",
            "[]", cancellationToken);

    public Task<string?> GetRawAsync(string subject, string version, CancellationToken cancellationToken) =>
        ResolveRawAsync(new SchemaRef(subject, version), cancellationToken);

    public Task<string> GetDiffAsync(string subject, string from, string to, CancellationToken cancellationToken) =>
        GetUnwrappedAsync(
            $"{Base}/api/v1/namespaces/{Ns}/schemas/{Uri.EscapeDataString(subject)}/diff" +
            $"?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}",
            "{}", cancellationToken);

    public async Task<SchemaCompatibilityResult> CheckCompatibilityAsync(
        string subject, JsonElement content, CancellationToken cancellationToken) {
        try {
            var body = new { type = "Json", content };
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using StringContent payload = new(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            string url = $"{Base}/api/v1/namespaces/{Ns}/schemas/" +
                $"{Uri.EscapeDataString(subject)}/compatibility";
            using HttpResponseMessage response = await client.PostAsync(url, payload, cancellationToken);
            string raw = await response.Content.ReadAsStringAsync(cancellationToken);

            if(!response.IsSuccessStatusCode) {
                // Vaultify may answer an incompatible candidate with a non-2xx +
                // an explanatory body — surface it as "incompatible", not "unknown".
                logger.LogInformation("Compatibility check for {Subject} returned {Status}",
                    subject, (int)response.StatusCode);
                return new SchemaCompatibilityResult(false, ExtractMessage(raw) ?? Truncate(raw));
            }

            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement data = doc.RootElement.TryGetProperty("data", out JsonElement d)
                ? d : doc.RootElement;
            bool? compatible = ReadBool(data, "compatible") ?? ReadBool(data, "isCompatible");
            string? detail = ReadString(data, "message") ?? ReadMessages(data);
            // A 2xx with no explicit flag means the registry accepted the candidate.
            return new SchemaCompatibilityResult(compatible ?? true, detail);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch(Exception ex) {
            logger.LogWarning(ex, "Compatibility check for {Subject} failed", subject);
            return new SchemaCompatibilityResult(null, null);
        }
    }

    /// <summary>GETs a Vaultify resource and returns the raw JSON of its <c>data</c>
    /// node (or the whole body if there is no envelope); <paramref name="fallback"/>
    /// on any non-success / transport failure so callers degrade gracefully.</summary>
    private async Task<string> GetUnwrappedAsync(
        string url, string fallback, CancellationToken cancellationToken) {
        try {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
            if(!response.IsSuccessStatusCode) {
                logger.LogWarning("Schema registry GET {Url} returned {Status}",
                    url, (int)response.StatusCode);
                return fallback;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            JsonElement data = doc.RootElement.TryGetProperty("data", out JsonElement d)
                ? d : doc.RootElement;
            return data.GetRawText();
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch(Exception ex) {
            logger.LogWarning(ex, "Schema registry GET {Url} failed", url);
            return fallback;
        }
    }

    private static bool? ReadBool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out JsonElement v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out JsonElement v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>Joins a <c>messages</c> array (if present) into one detail string.</summary>
    private static string? ReadMessages(JsonElement el) {
        if(el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty("messages", out JsonElement arr)
            || arr.ValueKind != JsonValueKind.Array)
            return null;
        var parts = arr.EnumerateArray()
            .Where(m => m.ValueKind == JsonValueKind.String)
            .Select(m => m.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        return parts.Length > 0 ? string.Join("; ", parts!) : null;
    }

    /// <summary>Best-effort pull of a human message out of an error body
    /// (ProblemDetails <c>detail</c>/<c>title</c> or a bare <c>message</c>).</summary>
    private static string? ExtractMessage(string raw) {
        if(string.IsNullOrWhiteSpace(raw))
            return null;
        try {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;
            return ReadString(root, "detail")
                ?? ReadString(root, "message")
                ?? ReadString(root, "title");
        }
        catch(JsonException) {
            return null;
        }
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];
}
