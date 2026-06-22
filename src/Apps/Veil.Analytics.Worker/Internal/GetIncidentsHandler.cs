using Tyto.Rpc;
using Veil.Analytics.Intelligence;

namespace Veil.Analytics.Worker.Internal;

/// <summary>
/// Serves the live AI incident feed over Tyto RPC (Phase 12). Reads the
/// process-local <see cref="IncidentStore"/> ring — empty when intelligence is
/// disabled. The control plane (Veil.Api) is the caller; this replaces the
/// bespoke <c>/intelligence/incidents</c> HTTP proxy.
/// </summary>
public sealed class GetIncidentsHandler(IncidentStore store)
    : IRpcRequestHandler<GetIncidentsRequest, TrafficIncident[]> {

    public Task<RpcResult<TrafficIncident[]>> HandleAsync(
        GetIncidentsRequest request,
        CancellationToken cancellationToken) {

        int limit = Math.Clamp(request.Limit, 1, 200);
        TrafficIncident[] incidents = [.. store.Recent(limit)];
        return Task.FromResult(RpcResult<TrafficIncident[]>.Success(incidents));
    }
}
