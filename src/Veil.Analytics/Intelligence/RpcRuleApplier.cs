using Microsoft.Extensions.Logging;
using Tyto.Rpc;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Applies an AI-suggested rule by calling the control plane (Veil.Api) over
/// Tyto RPC (Phase 12, <see cref="ApplyAiRuleRequest"/>). Replaces the bespoke
/// two-call HTTP flow: the server now resolves the zone by hostname and maps
/// the suggestion onto the real rule vocabulary in-process, so the worker only
/// forwards the raw suggestion + shadow flag.
///
/// Authentication is carried by the RPC client's <c>X-Api-Key</c> default header
/// (configured at registration), matching the control plane's API-key scheme.
/// </summary>
public sealed class RpcRuleApplier(
    IRpcClient rpcClient,
    ILogger<RpcRuleApplier> logger) : IRuleApplier {

    public async Task ApplyAsync(string zone, SuggestedRule rule, bool shadow, CancellationToken cancellationToken) {
        try {
            ApplyAiRuleRequest request = new(zone, rule, shadow);
            RpcResult<ApplyAiRuleResult> result =
                await rpcClient.CallAsync<ApplyAiRuleRequest, ApplyAiRuleResult>(request, cancellationToken);

            result.Match(
                onSuccess: outcome => {
                    if(outcome.Applied)
                        logger.LogInformation("AI rule {Mode} on {Zone}: {Type}={Value} → {Action}",
                            shadow ? "SHADOW" : "ENFORCE", zone, rule.ConditionType, rule.Value, outcome.Action);
                    else
                        logger.LogWarning("AI rule for {Zone} skipped: {Reason}", zone, outcome.Reason);
                    return outcome;
                },
                onError: error => {
                    logger.LogWarning("AI rule apply on {Zone} failed: {Code} {Description}",
                        zone, error.Code, error.Description);
                    return null!;
                });
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            // Shutting down.
        }
        catch(Exception ex) {
            logger.LogWarning(ex, "AI rule apply failed for {Zone}", zone);
        }
    }
}
