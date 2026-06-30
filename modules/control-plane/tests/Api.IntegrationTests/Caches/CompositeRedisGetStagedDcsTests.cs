using Api.Infrastructure.Caches;
using Domain.FeatureFlags;
using Infrastructure.Caches.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Api.IntegrationTests.Caches;

/// <summary>
/// C3b-1 Part 2 (coordinator API): CompositeRedisCacheService.GetStagedDcsAsync returns, per
/// DcId, whether THAT DC's Redis holds the staged version flag:{id}:v{ts}.
///
/// Two DCs are simulated by two RedisCacheService instances bound to two logical Redis DB
/// indexes (0 = west, 1 = east) on a single throwaway Redis. Staging into west's DB only must
/// produce a per-DcId map showing present in west and absent in east.
///
/// Requires a real Redis on a NON-default port (6383). Override via C3B1_REDIS env var. Fails
/// loudly if Redis is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public class CompositeRedisGetStagedDcsTests : IAsyncLifetime
{
    private const string DefaultConnection = "localhost:6383";
    private const string WestDcId = "dc-west";
    private const string EastDcId = "dc-east";

    private ConnectionMultiplexer? _mux;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("C3B1_REDIS") ?? DefaultConnection;

    public async Task InitializeAsync()
    {
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 2000;

        _mux = await ConnectionMultiplexer.ConnectAsync(options);
        if (!_mux.IsConnected)
        {
            throw new InvalidOperationException(
                $"No Redis reachable at '{ConnectionString}'. Start one with: " +
                "docker run -d --rm -p 6383:6379 --name c3b1-redis redis:7-alpine " +
                "(or set the C3B1_REDIS env var).");
        }
    }

    public Task DisposeAsync()
    {
        _mux?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetStagedDcs_ReportsPerDc_StagedPresence()
    {
        // west -> logical db 0, east -> logical db 1 (independent keyspaces on one server)
        var west = new RedisCacheService(new TestRedisClient(_mux!, db: 0));
        var east = new RedisCacheService(new TestRedisClient(_mux!, db: 1));

        var sut = new CompositeRedisCacheService(
            new[]
            {
                new DcCacheService(WestDcId, west),
                new DcCacheService(EastDcId, east)
            },
            NullLogger<CompositeRedisCacheService>.Instance);

        var envId = Guid.NewGuid();
        var flagId = Guid.NewGuid();
        var ts = 12_000L;

        // stage the version into WEST's Redis only
        await west.StageFlagAsync(NewFlag(envId, flagId, ts), ts);

        var map = await sut.GetStagedDcsAsync(flagId, ts);

        Assert.Equal(2, map.Count);
        Assert.True(map[WestDcId], "west staged the version, so its DC should report present");
        Assert.False(map[EastDcId], "east never staged the version, so its DC should report absent");

        // sanity: the ICacheService probe is local-first (west = first instance) -> true
        Assert.True(await sut.HasStagedFlagAsync(flagId, ts));

        // cleanup
        await _mux!.GetDatabase(0).KeyDeleteAsync(RedisCaches.FlagVersion(flagId, ts));
    }

    private static FeatureFlag NewFlag(Guid envId, Guid flagId, long ts) => new()
    {
        Id = flagId,
        EnvId = envId,
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
    };

    private sealed class TestRedisClient(IConnectionMultiplexer connection, int db) : IRedisClient
    {
        public IConnectionMultiplexer Connection { get; } = connection;

        public IDatabase GetDatabase() => Connection.GetDatabase(db);
    }
}
