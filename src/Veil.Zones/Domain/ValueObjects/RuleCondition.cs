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
// Persistence (jsonb) discriminator. This is independent of the edge wire
// discriminator (`Type`, which is path_prefix/path_exact for a path match).
[JsonDerivedType(typeof(PathMatchCondition), "path_match")]
[JsonDerivedType(typeof(PathRegexMatchCondition), "path_regex")]
[JsonDerivedType(typeof(HeaderMatchCondition), "header")]
[JsonDerivedType(typeof(UserAgentMatchCondition), "user_agent")]
[JsonDerivedType(typeof(MethodMatchCondition), "method")]
[JsonDerivedType(typeof(QueryRegexMatchCondition), "query_regex")]
[JsonDerivedType(typeof(HeaderRegexMatchCondition), "header_regex")]
[JsonDerivedType(typeof(BodyRegexMatchCondition), "body_regex")]
[JsonDerivedType(typeof(BodyJsonMatchCondition), "body_json")]
[JsonDerivedType(typeof(Ja3MatchCondition), "ja3")]
[JsonDerivedType(typeof(Ja4MatchCondition), "ja4")]
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

/// <summary>HTTP metodu eşleşmesi (case-insensitive, ör. "POST").</summary>
public sealed record MethodMatchCondition(string Method) : RuleCondition {
    public override string Type => "method";
}

/// <summary>Sorgu string'i (query) regex eşleşmesi.</summary>
public sealed record QueryRegexMatchCondition(string Regex) : RuleCondition {
    public override string Type => "query_regex";
}

/// <summary>Belirli bir header değerinde regex eşleşmesi.</summary>
public sealed record HeaderRegexMatchCondition(string Name, string Regex) : RuleCondition {
    public override string Type => "header_regex";
}

/// <summary>İstek gövdesinde regex eşleşmesi (gövde tamponlamasını zorlar).</summary>
public sealed record BodyRegexMatchCondition(string Regex) : RuleCondition {
    public override string Type => "body_regex";
}

/// <summary>JSON gövdesinde tek bir alanda regex eşleşmesi. `Path` noktalı
/// (`$.user.name`). Tüm gövdeyi taramaktan çok daha az yanlış pozitif üretir.</summary>
public sealed record BodyJsonMatchCondition(string Path, string Regex) : RuleCondition {
    public override string Type => "body_json";
}

// ── TLS fingerprint Conditions ───────────────────────────────────────

/// <summary>JA3 TLS istemci parmak izi (MD5 hex) ile eşleşme. Yalnızca HTTPS.</summary>
public sealed record Ja3MatchCondition(string Fingerprint) : RuleCondition {
    public override string Type => "ja3";
}

/// <summary>JA4 TLS istemci parmak izi (FoxIO) ile eşleşme. Yalnızca HTTPS.</summary>
public sealed record Ja4MatchCondition(string Fingerprint) : RuleCondition {
    public override string Type => "ja4";
}
