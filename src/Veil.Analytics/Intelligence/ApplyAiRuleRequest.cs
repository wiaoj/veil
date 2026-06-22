using Tyto.Rpc;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Tyto RPC contract for applying an AI-suggested rule (Phase 12). The worker
/// owns the live stream; Zones live in Veil.Api, so the worker hands the raw
/// suggestion to the control plane over RPC-over-HTTP and the server resolves
/// the zone by hostname + creates the rule in-process. Replaces the worker's
/// bespoke two-call HTTP flow (GET /v1/zones + POST /v1/zones/{id}/rules).
///
/// The call is authenticated by the control plane's existing API-key scheme —
/// the worker attaches <c>X-Api-Key</c> as an RPC default header — so the
/// privileged rule-creation path keeps its auth.
/// </summary>
/// <param name="Zone">The zone hostname (as seen in the edge logs).</param>
/// <param name="Rule">The suggested rule (condition + action vocabulary).</param>
/// <param name="Shadow">When true the rule is created observe-only (Log action).</param>
public sealed record ApplyAiRuleRequest(string Zone, SuggestedRule Rule, bool Shadow)
    : IRpcRequest<ApplyAiRuleResult>;

/// <summary>Outcome of an <see cref="ApplyAiRuleRequest"/>.</summary>
/// <param name="Applied">True when a rule was created.</param>
/// <param name="Action">The resolved edge action (Block/Challenge/RateLimit/Log), or "none".</param>
/// <param name="Reason">Why it was skipped, when <paramref name="Applied"/> is false.</param>
public sealed record ApplyAiRuleResult(bool Applied, string Action, string? Reason);
