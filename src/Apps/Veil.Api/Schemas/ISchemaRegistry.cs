using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Veil.Shared;

namespace Veil.Api.Schemas;

/// <summary>Reference to a schema stored in the registry.</summary>
public sealed record SchemaRef(string Subject, string Version);

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
}

/// <summary>No-op registry used when no Vaultify URL is configured.</summary>
public sealed class DisabledSchemaRegistry : ISchemaRegistry {
    public bool IsEnabled => false;

    public Task<Result<SchemaRef>> RegisterAsync(string subject, string version, JsonElement content, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<SchemaRef>(
            Error.Validation("Schema.RegistryDisabled", "No schema registry is configured (Vaultify:BaseUrl).")));

    public Task<string?> ResolveRawAsync(SchemaRef reference, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
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

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];
}
