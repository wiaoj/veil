using Tyto.Rpc;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Tyto RPC contract for the live AI incident feed. The incident store is
/// process-local to the analytics worker, so the control plane calls this over
/// RPC-over-HTTP (Phase 12) instead of a bespoke <see cref="System.Net.Http.HttpClient"/>
/// proxy. Read-only and idempotent — degrades to an empty feed on failure.
/// </summary>
/// <param name="Limit">Maximum incidents to return, newest first (clamped 1..200).</param>
public sealed record GetIncidentsRequest(int Limit = 50) : IRpcRequest<TrafficIncident[]>;
