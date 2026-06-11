namespace Veil.EdgeNodes.Contracts;

/// <summary>
/// Authenticates an edge node by its public id and plaintext token. The
/// narrow surface other processes (e.g. the analytics ingest worker) depend
/// on instead of the module's persistence internals; a successful
/// verification also counts as node contact (last-seen refresh is the
/// implementation's concern).
/// </summary>
public interface IEdgeNodeTokenVerifier {
    ValueTask<EdgeNodeTokenVerdict> VerifyAsync(string nodeId, string token, CancellationToken cancellationToken);
}

public enum EdgeNodeTokenVerdict {
    /// <summary>Unknown node id or token mismatch.</summary>
    Invalid,
    /// <summary>Credentials are correct but the node is disabled.</summary>
    Disabled,
    Valid,
}
