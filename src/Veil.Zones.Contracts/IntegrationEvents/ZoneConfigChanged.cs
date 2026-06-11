using Tyto;

namespace Veil.Zones.Contracts.IntegrationEvents;

/// <summary>
/// Published on the bus whenever a zone's effective edge configuration
/// changes (creation included). Carries the public (obfuscated) zone id —
/// integration events are cross-module contracts and never leak raw ids.
/// </summary>
[Message("zones.config-changed", 1)]
public sealed record ZoneConfigChanged(string ZoneId, DateTimeOffset OccurredAtUtc) : IEvent;