using Tyto.Rpc;
using Veil.Analytics.Intelligence;

namespace Veil.Api.Internal;

/// <summary>
/// Control-plane side of the AI rule-application RPC (Phase 12). The worker's
/// auto-apply path calls this over Tyto RPC-over-HTTP under /rpc, behind the
/// default API-key auth. Delegates the actual zone resolution + rule creation to
/// <see cref="AiRuleService"/>, which is shared with the dashboard's manual
/// one-click apply REST endpoint.
/// </summary>
public sealed class ApplyAiRuleHandler(AiRuleService service)
    : IRpcRequestHandler<ApplyAiRuleRequest, ApplyAiRuleResult> {

    public async Task<RpcResult<ApplyAiRuleResult>> HandleAsync(
        ApplyAiRuleRequest request,
        CancellationToken cancellationToken) {

        ApplyAiRuleResult result =
            await service.ApplyAsync(request.Zone, request.Rule, request.Shadow, cancellationToken);
        return RpcResult<ApplyAiRuleResult>.Success(result);
    }
}
