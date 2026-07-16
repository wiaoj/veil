namespace Veil.Api.Schemas;

/// <summary>
/// Typed view of the <c>Vaultify</c> configuration section. Vaultify (the schema
/// registry, sibling service) is Veil's schema store: uploads go there, and the
/// concrete schema is resolved from there when a config snapshot is built. Unset
/// <see cref="BaseUrl"/> disables the schema-reference feature.
/// </summary>
public sealed record VaultifyOptions {
    public const string SectionName = "Vaultify";

    /// <summary>Base URL of the Vaultify API, e.g. <c>http://vaultify:8080</c>.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Namespace schemas live under (multi-tenant isolation).</summary>
    public string Namespace { get; init; } = "veil";
}
