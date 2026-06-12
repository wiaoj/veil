using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using Veil.Api.ConfigSync;
using Veil.Certificates;
using Veil.EdgeNodes.Domain;
using Veil.EdgeNodes.Domain.Enums;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Wiaoj.Primitives.Cryptography.Hashing;

namespace Veil.Api.Acme;

/// <summary>
/// Publishes the active HTTP-01 challenge set to every enabled edge node so
/// any of them can answer the ACME validation request. Bodies are signed
/// with the same shared HMAC key as config pushes
/// (<c>ConfigSync:PushHmacKey</c>).
/// </summary>
public sealed class EdgeChallengePublisher(
    IDbContextFactory<EdgeNodesDbContext> nodesDbFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<ConfigSyncOptions> configSyncOptions,
    IOptions<CertificatesOptions> certificatesOptions,
    ILogger<EdgeChallengePublisher> logger) {

    public sealed record ChallengeEntry(string Token, string KeyAuthorization);

    /// <summary>
    /// Replaces the challenge set on all enabled nodes. Returns true when at
    /// least one node accepted (ACME validation needs only one reachable
    /// node behind the hostname).
    /// </summary>
    public async Task<bool> PublishAsync(
        IReadOnlyList<ChallengeEntry> challenges,
        CancellationToken cancellationToken) {
        byte[]? pushKey = ReadPushKey();
        if(pushKey is null) {
            logger.LogWarning("ACME challenge publish skipped: ConfigSync:PushHmacKey not configured");
            return false;
        }

        string json = JsonSerializer.Serialize(
            new { challenges },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        string signature = HmacSha256Hash.Compute(pushKey, jsonBytes).ToHexString().ToLower();

        await using EdgeNodesDbContext nodesDb = await nodesDbFactory.CreateDbContextAsync(cancellationToken);
        List<EdgeNode> nodes = await nodesDb.EdgeNodes
            .AsNoTracking()
            .Where(n => n.Status != EdgeNodeStatus.Disabled)
            .ToListAsync(cancellationToken);

        bool anySucceeded = false;
        foreach(EdgeNode node in nodes) {
            string url = node.Address.ToString().TrimEnd('/') + certificatesOptions.Value.ChallengePushPath;
            try {
                HttpClient client = httpClientFactory.CreateClient(ConfigSyncService.HttpClientName);
                using HttpRequestMessage request = new(HttpMethod.Post, url) {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation(configSyncOptions.Value.SignatureHeader, signature);

                using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                if(response.IsSuccessStatusCode) {
                    anySucceeded = true;
                }
                else {
                    logger.LogWarning("ACME challenge publish to {Node} failed: HTTP {Status}",
                        node.Name, (int)response.StatusCode);
                }
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
                throw;
            }
            catch(Exception ex) {
                logger.LogWarning("ACME challenge publish to {Node} failed: {Error}", node.Name, ex.Message);
            }
        }

        return anySucceeded;
    }

    public Task<bool> ClearAsync(CancellationToken cancellationToken) {
        return PublishAsync([], cancellationToken);
    }

    private byte[]? ReadPushKey() {
        string? hex = configSyncOptions.Value.PushHmacKey;
        if(string.IsNullOrWhiteSpace(hex)) return null;
        try {
            byte[] key = Convert.FromHexString(hex);
            return key.Length == 32 ? key : null;
        }
        catch(FormatException) {
            return null;
        }
    }
}
