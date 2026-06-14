namespace Veil.Auth.Audit;

/// <summary>
/// Writes audit records. Outcomes are common enough to have named outcome
/// constants; the writer persists each record to the append-only audit table.
/// </summary>
public interface IAuditLogger {
    Task WriteAsync(
        string action,
        string outcome,
        string? actor = null,
        string? actorIp = null,
        string? target = null,
        string? detail = null,
        CancellationToken cancellationToken = default);
}

public static class AuditOutcome {
    public const string Success = "success";
    public const string Failure = "failure";
}

public static class AuditActions {
    public const string LoginSuccess = "auth.login.success";
    public const string LoginFailure = "auth.login.failure";
    public const string LoginLockedOut = "auth.login.locked_out";
    public const string ApiKeyCreated = "auth.apikey.created";
    public const string ApiKeyRevoked = "auth.apikey.revoked";
}
