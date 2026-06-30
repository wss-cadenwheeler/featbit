using Domain.FeatureFlags;
using Infrastructure.Caches.Redis;
using StackExchange.Redis;

namespace Application.IntegrationTests.Caches;

/// <summary>
/// C3b-1 Part 2: RedisCacheService.HasStagedFlagAsync probes whether THIS Redis holds the
/// staged version key flag:{id}:v{ts} (written by StageFlagAsync).
///
/// Requires a real Redis instance. The orchestrating issue spins one up on a NON-default
/// port (6383). Override via the C3B1_REDIS env var. Fails loudly if Redis is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public class RedisHasStagedFlagTests : IAsyncLifetime
{
    private const string DefaultConnection = "localhost:6383";

    private ConnectionMultiplexer? _mux;
    private RedisCacheService? _sut;

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

        _sut = new RedisCacheService(new TestRedisClient(_mux));
    }

    public Task DisposeAsync()
    {
        _mux?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HasStagedFlag_True_After_Stage_False_Otherwise()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var flagId = Guid.NewGuid();
        var ts = 9_000L;

        // not staged yet
        Assert.False(await sut.HasStagedFlagAsync(flagId, ts));

        // stage the version
        await sut.StageFlagAsync(NewFlag(envId, flagId, ts), ts);

        // present for this exact (id, ts)
        Assert.True(await sut.HasStagedFlagAsync(flagId, ts));

        // a different ts for the same flag is NOT staged
        Assert.False(await sut.HasStagedFlagAsync(flagId, ts + 1));

        // a different flag id is NOT staged
        Assert.False(await sut.HasStagedFlagAsync(Guid.NewGuid(), ts));

        // cleanup
        await db.KeyDeleteAsync(RedisCaches.FlagVersion(flagId, ts));
    }

    private static FeatureFlag NewFlag(Guid envId, Guid flagId, long ts) => new()
    {
        Id = flagId,
        EnvId = envId,
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
    };

    private sealed class TestRedisClient(IConnectionMultiplexer connection) : IRedisClient
    {
        public IConnectionMultiplexer Connection { get; } = connection;

        public IDatabase GetDatabase() => Connection.GetDatabase();
    }
}
