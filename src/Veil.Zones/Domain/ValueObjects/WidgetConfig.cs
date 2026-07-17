using System.Security.Cryptography;

namespace Veil.Zones.Domain.ValueObjects;

/// <summary>
/// Per-zone credentials for the embeddable, self-hosted bot-verification widget
/// (Veil's own "verify I'm human", no third-party service). The <see cref="SiteKey"/>
/// is public (embedded in the page); the <see cref="Secret"/> is private and gates
/// the <c>/_veil/siteverify</c> endpoint the origin backend calls. Both are pushed
/// to the edge in the config snapshot, alongside TLS material.
/// </summary>
public sealed class WidgetConfig {
    /// <summary>Valid theme hints for the rendered widget.</summary>
    private static readonly HashSet<string> Themes = ["auto", "light", "dark"];

    public bool Enabled { get; }
    public string SiteKey { get; }
    public string Secret { get; }
    public string Theme { get; }

    /// <summary>True once keys have been generated (a widget cannot serve without them).</summary>
    public bool HasKeys => !string.IsNullOrEmpty(this.SiteKey) && !string.IsNullOrEmpty(this.Secret);

    /// <summary>Off, no keys — the default for a new zone.</summary>
    public static WidgetConfig Disabled => new(false, "", "", "auto");

    private WidgetConfig(bool enabled, string siteKey, string secret, string theme) {
        this.Enabled = enabled;
        this.SiteKey = siteKey;
        this.Secret = secret;
        this.Theme = Themes.Contains(theme) ? theme : "auto";
    }

    /// <summary>Persistence-only factory: trusts previously validated stored data.</summary>
    internal static WidgetConfig Restore(bool enabled, string? siteKey, string? secret, string? theme) =>
        new(enabled, siteKey ?? "", secret ?? "", theme ?? "auto");

    /// <summary>Updates the enabled flag and theme, keeping existing keys.</summary>
    public WidgetConfig With(bool enabled, string? theme) =>
        new(enabled, this.SiteKey, this.Secret, theme ?? this.Theme);

    /// <summary>Generates a fresh site key + secret (rotation), keeping enabled/theme.</summary>
    public WidgetConfig WithNewKeys() =>
        new(this.Enabled, GenerateKey("vw_site_"), GenerateKey("vw_sec_"), this.Theme);

    private static string GenerateKey(string prefix) {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return prefix + Convert.ToHexStringLower(buf);
    }
}
