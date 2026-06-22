using Tyto;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// Published on the bus whenever the analysis loop detects an incident
/// (Phase 12 Slice 3). The alerting sinks (webhook, SIEM) subscribe as
/// independent handlers, so a sink outage never blocks the analysis loop or
/// the other sink. In-memory transport today (publisher + subscribers are
/// co-located in the worker); swappable to a broker without touching callers.
/// </summary>
[Message("intelligence.incident-raised", 1)]
public sealed record IncidentRaised(TrafficIncident Incident) : IEvent;
