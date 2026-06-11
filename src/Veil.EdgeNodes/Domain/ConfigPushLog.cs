using Veil.EdgeNodes.Domain.ValueObjects;
using Wiaoj.Ddd;

namespace Veil.EdgeNodes.Domain;

/// <summary>
/// One config push attempt to an edge node, written by the ConfigSync worker
/// (Phase 3). Append-only.
/// </summary>
public sealed class ConfigPushLog : Entity<ConfigPushLogId> {
    public EdgeNodeId EdgeNodeId { get; private set; }
    public bool Succeeded { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset PushedAtUtc { get; private set; }

    private ConfigPushLog() { }

    public static ConfigPushLog Record(
        EdgeNodeId edgeNodeId,
        bool succeeded,
        string? error,
        DateTimeOffset pushedAtUtc) {
        return new ConfigPushLog {
            Id = ConfigPushLogId.New(),
            EdgeNodeId = edgeNodeId,
            Succeeded = succeeded,
            Error = error,
            PushedAtUtc = pushedAtUtc
        };
    }
}
