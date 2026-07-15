using Microsoft.Extensions.DependencyInjection;
using Testcontainers.ClickHouse;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace Veil.IntegrationTests;

/// <summary>Shared helpers for the integration suite.</summary>
internal static class TestInfra {
    /// <summary>A real <see cref="IHttpClientFactory"/> for the ClickHouse clients.</summary>
    public static IHttpClientFactory HttpClientFactory { get; } =
        new ServiceCollection().AddHttpClient().BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>();
}

/// <summary>Spins up a throwaway PostgreSQL once per test collection.</summary>
public sealed class PostgresFixture : IAsyncLifetime {
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString => this._container.GetConnectionString();

    public Task InitializeAsync() => this._container.StartAsync();
    public Task DisposeAsync() => this._container.DisposeAsync().AsTask();
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>Spins up a throwaway ClickHouse once per test collection.</summary>
public sealed class ClickHouseFixture : IAsyncLifetime {
    private readonly ClickHouseContainer _container = new ClickHouseBuilder()
        .WithImage("clickhouse/clickhouse-server:24-alpine")
        .WithUsername("veil")
        .WithPassword("veil")
        .WithDatabase("veil")
        .Build();

    public string Host => this._container.Hostname;
    public ushort HttpPort => this._container.GetMappedPublicPort(8123);

    public Task InitializeAsync() => this._container.StartAsync();
    public Task DisposeAsync() => this._container.DisposeAsync().AsTask();
}

[CollectionDefinition(nameof(ClickHouseCollection))]
public sealed class ClickHouseCollection : ICollectionFixture<ClickHouseFixture>;

/// <summary>Spins up a throwaway Redis once per test collection.</summary>
public sealed class RedisFixture : IAsyncLifetime {
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public string ConnectionString => this._container.GetConnectionString();

    public Task InitializeAsync() => this._container.StartAsync();
    public Task DisposeAsync() => this._container.DisposeAsync().AsTask();
}

[CollectionDefinition(nameof(RedisCollection))]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;
