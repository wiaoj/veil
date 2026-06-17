using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Veil.EdgeNodes.Contracts;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.Enums;
using Veil.EdgeNodes.Domain.ValueObjects;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Shared;
using Wiaoj.Primitives.Buffers;
using Wiaoj.Primitives.Cryptography.Hashing;

namespace Veil.EdgeNodes;

/// <summary>
/// DbContext-backed implementation of <see cref="IEdgeNodeTokenVerifier"/>:
/// decodes the public node id, compares the SHA-256 hash of the presented
/// token and refreshes last-seen (throttled — callers verify on every
/// batch/pull, writing the row each time would be twice-a-second churn).
/// </summary>
public sealed class EdgeNodeTokenVerifier(
    IDbContextFactory<EdgeNodesDbContext> dbFactory,
    IObfuscator<EdgeNodeId> obfuscator,
    TimeProvider timeProvider) : IEdgeNodeTokenVerifier {

    private static readonly TimeSpan MarkSeenInterval = TimeSpan.FromMinutes(1);

    public async ValueTask<EdgeNodeTokenVerdict> VerifyAsync(string nodeId, string token, CancellationToken cancellationToken) {
        if(!obfuscator.TryDecode(nodeId, out EdgeNodeId edgeNodeId) || string.IsNullOrEmpty(token))
            return EdgeNodeTokenVerdict.Invalid;

        await using EdgeNodesDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        EdgeNode? node = await db.EdgeNodes
            .FirstOrDefaultAsync(n => n.Id == edgeNodeId, cancellationToken);

        HexString actualTokenHash = Sha256Hash.Compute(token).ToHexStringLower();

        if(node is null) {
            using ValueBuffer<byte> fakeHash = ValueBuffer.Create32(stackalloc byte[32]);
            CryptographicOperations.FixedTimeEquals(fakeHash, actualTokenHash.ToBytes());
            return EdgeNodeTokenVerdict.Invalid;
        }

        if(!node.TokenHash.FixedTimeEquals(actualTokenHash))
            return EdgeNodeTokenVerdict.Invalid;

        if(node.Status is EdgeNodeStatus.Disabled)
            return EdgeNodeTokenVerdict.Disabled;

        DateTimeOffset now = timeProvider.GetUtcNow();
        node.MarkSeen(now);
        await db.SaveChangesAsync(cancellationToken);

        return EdgeNodeTokenVerdict.Valid;
    }
}