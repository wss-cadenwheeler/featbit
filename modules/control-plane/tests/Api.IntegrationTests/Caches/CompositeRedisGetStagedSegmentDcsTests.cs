using Api.Infrastructure.Caches;
using Domain.Segments;
using Infrastructure.Caches.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Api.IntegrationTests.Caches;

/// <summary>
/// S2 (coordinator API): CompositeRedisCacheService.GetStagedSegmentDcsAsync returns, per
/// DcId, whether THAT DC's Redis holds the staged version segment:{id}:v{ts}. Segment
/// counterpart of <see cref="CompositeRedisGetStagedDcsTests"/>.
///
/// Two DCs are simulated by two RedisCacheService instances bound to two logical Redis DB
/// indexes (0 = west, 1 = east) on a single throwaway Redis. Staging into west's DB only must
/// produce a per-DcId map showing present in west and absent in east.
///
/// Requires a real Redis on a NON-default port (6388). Override via S2_REDIS env var. Fails
/// loudly if Redis is unreachable.
/// </summary>
public class CompositeRedisGetStagedSegmentDcsTests : IAsyncLifetime
{
    private const string DefaultConnection = "localhost:6388";
    private const string WestDcId = "dc-west";
    private const string EastDcId = "dc-east";

    private ConnectionMultiplexer? _mux;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("S2_REDIS") ?? DefaultConnection;

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
                "docker run -d --rm -p 6388:6379 --name s2-redis redis:7-alpine " +
                "(or set the S2_REDIS env var).");
        }
    }

    public Task DisposeAsync()
    {
        _mux?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetStagedSegmentDcs_ReportsPerDc_StagedPresence()
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

        var segmentId = Guid.NewGuid();
        var ts = 12_000L;

        // stage the version into WEST's Redis only
        await west.StageSegmentAsync(NewSegment(segmentId, ts), ts);

        var map = await sut.GetStagedSegmentDcsAsync(segmentId, ts);

        Assert.Equal(2, map.Count);
        Assert.True(map[WestDcId], "west staged the version, so its DC should report present");
        Assert.False(map[EastDcId], "east never staged the version, so its DC should report absent");

        // sanity: the ICacheService probe is local-first (west = first instance) -> true
        Assert.True(await sut.HasStagedSegmentAsync(segmentId, ts));

        // cleanup
        await _mux!.GetDatabase(0).KeyDeleteAsync(RedisCaches.SegmentVersion(segmentId, ts));
    }

    private static Segment NewSegment(Guid segmentId, long ts) => new(
        workspaceId: Guid.NewGuid(),
        envId: Guid.NewGuid(),
        name: "test-segment",
        key: "test-segment",
        type: SegmentType.EnvironmentSpecific,
        scopes: [],
        included: [],
        excluded: [],
        rules: [],
        description: string.Empty)
    {
        Id = segmentId,
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
    };

    private sealed class TestRedisClient(IConnectionMultiplexer connection, int db) : IRedisClient
    {
        public IConnectionMultiplexer Connection { get; } = connection;

        public IDatabase GetDatabase() => Connection.GetDatabase(db);
    }
}
