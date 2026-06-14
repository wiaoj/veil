namespace Veil.Auth.Audit;

/// <summary>
/// An immutable audit record of a security-relevant control-plane action
/// (authentication, API-key lifecycle, account lockout). Append-only: rows are
/// never updated or deleted in normal operation, so the table is a tamper-
/// evident history for incident response and compliance.
/// </summary>
public sealed class AuditEvent {
    public Guid Id { get; private set; }
    public DateTimeOffset TimestampUtc { get; private set; }

    /// <summary>Dotted action name, e.g. <c>auth.login.success</c>.</summary>
    public string Action { get; private set; } = default!;

    /// <summary><c>success</c> or <c>failure</c>.</summary>
    public string Outcome { get; private set; } = default!;

    /// <summary>Who acted — email, user id, or <c>system</c>. May be unknown.</summary>
    public string? Actor { get; private set; }

    /// <summary>Source IP of the request, when available.</summary>
    public string? ActorIp { get; private set; }

    /// <summary>What was acted on — e.g. an API key id or target email.</summary>
    public string? Target { get; private set; }

    /// <summary>Free-form context; never secrets.</summary>
    public string? Detail { get; private set; }

    private AuditEvent() { }

    public static AuditEvent Record(
        DateTimeOffset timestampUtc,
        string action,
        string outcome,
        string? actor,
        string? actorIp,
        string? target,
        string? detail) =>
        new() {
            Id = Guid.CreateVersion7(),
            TimestampUtc = timestampUtc,
            Action = action,
            Outcome = outcome,
            Actor = actor,
            ActorIp = actorIp,
            Target = target,
            Detail = detail
        };
}
