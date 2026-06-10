using System.Text.Json.Serialization;
using Veil.Zones.Domain.Enums;

namespace Veil.Zones.Domain.ValueObjects;

/// <summary>
/// Kural koşulu discriminated union. Edge node, bu koşulları
/// gelen request'e karşı değerlendirir.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IpMatchCondition), "ip_match")]
[JsonDerivedType(typeof(IpRangeMatchCondition), "ip_range")]
[JsonDerivedType(typeof(CountryMatchCondition), "country")]
[JsonDerivedType(typeof(AsnMatchCondition), "asn")]
[JsonDerivedType(typeof(PathMatchCondition), "path_match")] // Uses path_prefix/path_exact conceptually, but for deserialization we need one or two. Let's map it. Actually, wait. The type discriminator in Rust is "path_prefix" or "path_exact". If we need dynamic discriminator, `JsonDerivedType` might not be enough for PathMatchCondition if it has two discriminators depending on the mode.
public abstract record RuleCondition {
    /// <summary>Edge config serialization'da kullanılan type discriminator.</summary>
    public abstract string Type { get; }
}

// ── IP Conditions ────────────────────────────────────────────────────

/// <summary>Tek bir IP adresiyle eşleşme (v4 veya v6).</summary>
public sealed record IpMatchCondition(string Ip) : RuleCondition {
    public override string Type => "ip_match";
}

/// <summary>CIDR notasyonuyla IP aralığı eşleşmesi (ör: "192.168.0.0/16").</summary>
public sealed record IpRangeMatchCondition(string Cidr) : RuleCondition {
    public override string Type => "ip_range";
}

// ── Geo Conditions ───────────────────────────────────────────────────

/// <summary>ISO 3166-1 alpha-2 ülke koduyla eşleşme.</summary>
public sealed record CountryMatchCondition(string CountryCode) : RuleCondition {
    public override string Type => "country";
}

/// <summary>BGP Autonomous System numarasıyla eşleşme.</summary>
public sealed record AsnMatchCondition(int Asn) : RuleCondition {
    public override string Type => "asn";
}

// ── Path Conditions ──────────────────────────────────────────────────

/// <summary>Path prefix veya exact match.</summary>
public sealed record PathMatchCondition(string Pattern, PathMatchMode Mode = PathMatchMode.Prefix) : RuleCondition {
    public override string Type => this.Mode switch {
        PathMatchMode.Exact => "path_exact",
        _ => "path_prefix"
    };
}


/// <summary>Path regex eşleşmesi.</summary>
public sealed record PathRegexMatchCondition(string Regex) : RuleCondition {
    public override string Type => "path_regex";
}

// ── Header / UA Conditions ───────────────────────────────────────────

/// <summary>Belirli bir HTTP header değeriyle eşleşme.</summary>
public sealed record HeaderMatchCondition(string Name, string Value) : RuleCondition {
    public override string Type => "header";
}

/// <summary>User-Agent string pattern eşleşmesi (case-insensitive contains).</summary>
public sealed record UserAgentMatchCondition(string Pattern) : RuleCondition {
    public override string Type => "user_agent";
}
