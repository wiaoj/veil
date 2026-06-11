namespace Veil.Shared;

/// <summary>
/// Typed view of the <c>Obfuscation</c> configuration section.
/// </summary>
public sealed record ObfuscationOptions {
    public const string SectionName = "Obfuscation";

    /// <summary>
    /// Seed for the Feistel id obfuscator. Changing it changes every public
    /// id, so it must be stable per environment.
    /// </summary>
    public string Seed { get; init; } = "vaultex-iam-server-dev-obfuscation-seed";
}
