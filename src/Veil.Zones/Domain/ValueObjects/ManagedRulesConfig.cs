using Veil.Zones.Domain.Enums;

namespace Veil.Zones.Domain.ValueObjects;

/// <summary>
/// Zone-level managed WAF signature toggles (OWASP-CRS-style families). The edge
/// evaluates the enabled categories against the request line, query, headers and
/// (optionally) the body. All-off means no managed inspection.
/// </summary>
public sealed class ManagedRulesConfig {
    public bool SqlInjection { get; }
    public bool Xss { get; }
    public bool PathTraversal { get; }
    /// <summary>Buffer + scan the request body in addition to URL/headers.</summary>
    public bool InspectBody { get; }
    public ManagedRuleAction Action { get; }

    /// <summary>True when at least one signature family is active.</summary>
    public bool IsActive => this.SqlInjection || this.Xss || this.PathTraversal;

    /// <summary>No managed inspection.</summary>
    public static ManagedRulesConfig Disabled => new(false, false, false, false, ManagedRuleAction.Block);

    private ManagedRulesConfig(bool sqlInjection, bool xss, bool pathTraversal, bool inspectBody, ManagedRuleAction action) {
        this.SqlInjection = sqlInjection;
        this.Xss = xss;
        this.PathTraversal = pathTraversal;
        this.InspectBody = inspectBody;
        this.Action = action;
    }

    public static ManagedRulesConfig Create(
        bool sqlInjection, bool xss, bool pathTraversal, bool inspectBody, ManagedRuleAction action) =>
        new(sqlInjection, xss, pathTraversal, inspectBody, action);

    /// <summary>Persistence-only factory for previously validated data.</summary>
    internal static ManagedRulesConfig Restore(
        bool sqlInjection, bool xss, bool pathTraversal, bool inspectBody, ManagedRuleAction action) =>
        new(sqlInjection, xss, pathTraversal, inspectBody, action);
}
