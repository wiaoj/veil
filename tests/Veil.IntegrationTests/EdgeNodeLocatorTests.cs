using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Veil.Api.ConfigSync;
using Veil.EdgeNodes.Domain;
using Wiaoj.Primitives.Cryptography.Hashing;
using Xunit;

namespace Veil.IntegrationTests;

/// <summary>
/// The locators split a registered node's *identity* (durable, in PostgreSQL)
/// from its *location* (ephemeral once the fleet is dynamic). These tests pin
/// that behaviour, and in particular the property the Redis mode exists for:
/// a node that stops renewing disappears on its own — no heartbeat table, no
/// reaper, no stale rows.
/// </summary>
[Collection(nameof(RedisCollection))]
public sealed class EdgeNodeLocatorTests(RedisFixture redis) : IAsyncLifetime {
    private const string Prefix = "veil:nodes:";

    private static EdgeNode Node(string address = "http://10.0.0.1:8080") =>
        EdgeNode.Register(
            "edge-1",
            new Uri(address),
            Sha256Hash.Compute("node-token").ToHexStringLower(),
            DateTimeOffset.UtcNow).Value;

    private static IOptions<ConfigSyncOptions> RedisOptions() =>
        Options.Create(new ConfigSyncOptions {
            Discovery = new DiscoveryOptions {
                Mode = DiscoveryMode.Redis,
                RedisKeyPrefix = Prefix,
                Scheme = "http",
            },
        });

    private IConnectionMultiplexer _redis = null!;

    public async Task InitializeAsync() {
        this._redis = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString + ",allowAdmin=true");
        // Each test starts from an empty registry.
        foreach(System.Net.EndPoint ep in this._redis.GetEndPoints())
            await this._redis.GetServer(ep).FlushDatabaseAsync();
    }

    public Task DisposeAsync() => this._redis.CloseAsync();

    [Fact]
    public async Task Static_locator_returns_the_registered_address() {
        StaticEdgeNodeLocator locator = new();

        IReadOnlyList<Uri> addresses = await locator.ResolveAsync(Node(), CancellationToken.None);

        Assert.Single(addresses);
        Assert.Equal("http://10.0.0.1:8080/", addresses[0].ToString());
    }

    [Fact]
    public async Task Redis_locator_returns_every_self_registered_node() {
        IDatabase db = this._redis.GetDatabase();
        // Two pods registered themselves under one fleet identity.
        await db.StringSetAsync($"{Prefix}a", """{"address":"http://10.0.3.7:8080"}""", TimeSpan.FromMinutes(1));
        await db.StringSetAsync($"{Prefix}b", """{"address":"http://10.0.3.8:8080"}""", TimeSpan.FromMinutes(1));

        RedisEdgeNodeLocator locator = new(this._redis, RedisOptions(), NullLogger<RedisEdgeNodeLocator>.Instance);
        IReadOnlyList<Uri> addresses = await locator.ResolveAsync(Node(), CancellationToken.None);

        Assert.Equal(2, addresses.Count);
        Assert.Contains(addresses, u => u.ToString() == "http://10.0.3.7:8080/");
        Assert.Contains(addresses, u => u.ToString() == "http://10.0.3.8:8080/");
    }

    [Fact]
    public async Task Redis_locator_accepts_a_bare_host_port_and_applies_the_scheme() {
        await this._redis.GetDatabase().StringSetAsync($"{Prefix}c", "10.0.3.9:8080", TimeSpan.FromMinutes(1));

        RedisEdgeNodeLocator locator = new(this._redis, RedisOptions(), NullLogger<RedisEdgeNodeLocator>.Instance);
        IReadOnlyList<Uri> addresses = await locator.ResolveAsync(Node(), CancellationToken.None);

        Assert.Single(addresses);
        Assert.Equal("http://10.0.3.9:8080/", addresses[0].ToString());
    }

    /// <summary>
    /// The whole reason Redis is the right store for location: expiry is the
    /// deregistration mechanism. A dead pod stops renewing and vanishes — this
    /// is what a PostgreSQL row could not do without a hand-written reaper.
    /// </summary>
    [Fact]
    public async Task A_node_that_stops_renewing_expires_out_of_the_registry() {
        IDatabase db = this._redis.GetDatabase();
        await db.StringSetAsync($"{Prefix}dying", """{"address":"http://10.0.3.99:8080"}""",
            TimeSpan.FromSeconds(1));

        RedisEdgeNodeLocator locator = new(this._redis, RedisOptions(), NullLogger<RedisEdgeNodeLocator>.Instance);

        Assert.Single(await locator.ResolveAsync(Node(), CancellationToken.None));

        // The pod dies: nothing renews the key.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        Assert.Empty(await locator.ResolveAsync(Node(), CancellationToken.None));
    }

    [Fact]
    public async Task Empty_registry_resolves_to_nothing_rather_than_failing() {
        RedisEdgeNodeLocator locator = new(this._redis, RedisOptions(), NullLogger<RedisEdgeNodeLocator>.Instance);

        // The push loop treats "nothing reachable" as skip, not as a failed push.
        Assert.Empty(await locator.ResolveAsync(Node(), CancellationToken.None));
    }
}
