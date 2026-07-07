using Api.Application.ControlPlane;
using Api.Infrastructure.Caches;
using Application.Caches;
using Application.Configuration;
using Application.Services;
using Domain.FeatureFlags;
using Domain.Messages;
using Infrastructure.Caches.Redis;
using Infrastructure.Persistence.MongoDb;
using Infrastructure.Services.MongoDb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using StackExchange.Redis;

namespace Api.IntegrationTests.ControlPlane;

/// <summary>
/// #89 acceptance: DcBackfiller.BackfillDcAsync snapshots the source of truth (Mongo) once, then
/// awaits per-item targeted writes. If a fresher commit lands on the target DC's Redis AFTER that
/// snapshot was taken but BEFORE the backfill's (now-stale) write for the same flag/segment arrives,
/// an unconditional write would revert the DC back to the stale snapshot. This exercises the
/// only-advance guard end-to-end via the real DcBackfiller against real infrastructure: a real
/// MongoDB (the "stale snapshot" it reads) and a real Redis DC (holding a fresher committed value
/// than the Mongo snapshot, simulating the race).
///
/// Requires:
///  - MongoDB at mongodb://admin:password@localhost:27017 (unique throwaway DB, dropped on dispose).
///  - Redis on port 6391 (override via OA2_REDIS):
///      docker run -d --rm -p 6391:6379 --name featbit-test-redis-6391 redis:7-alpine
/// Fails loudly (not silently skips) if either is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DcBackfillerOnlyAdvanceTests : IAsyncLifetime
{
    private const string MongoConnectionString = "mongodb://admin:password@localhost:27017/?authSource=admin";
    private const string DefaultRedis = "localhost:6391";

    private const string DcA = "dc-a";
    private const string DcB = "dc-b";

    private readonly string _dbName = $"featbit_89_test_{Guid.NewGuid():N}";
    private readonly Guid _envId = Guid.NewGuid();

    private MongoDbClient _mongoDb = null!;
    private FeatureFlagService _flagService = null!;
    private SegmentService _segmentService = null!;

    private ConnectionMultiplexer _mux = null!;
    private RedisCacheService _dcbCache = null!; // db 1 -- the DC under backfill in these tests
    private CompositeRedisCacheService _composite = null!;

    private static string RedisConnectionString =>
        Environment.GetEnvironmentVariable("OA2_REDIS") ?? DefaultRedis;

    public async Task InitializeAsync()
    {
        var options = Options.Create(new MongoDbOptions
        {
            ConnectionString = MongoConnectionString,
            Database = _dbName
        });
        _mongoDb = new MongoDbClient(options);
        await _mongoDb.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));

        _flagService = new FeatureFlagService(_mongoDb);
        _segmentService = new SegmentService(_mongoDb, NullLogger<SegmentService>.Instance);

        var redisOptions = ConfigurationOptions.Parse(RedisConnectionString);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectTimeout = 2000;

        _mux = await ConnectionMultiplexer.ConnectAsync(redisOptions);
        if (!_mux.IsConnected)
        {
            throw new InvalidOperationException(
                $"No Redis reachable at '{RedisConnectionString}'. Start one with: " +
                "docker run -d --rm -p 6391:6379 --name featbit-test-redis-6391 redis:7-alpine " +
                "(or set the OA2_REDIS env var).");
        }

        var dcaCache = new RedisCacheService(new TestRedisClient(_mux, db: 0));
        _dcbCache = new RedisCacheService(new TestRedisClient(_mux, db: 1));

        _composite = new CompositeRedisCacheService(
            new[]
            {
                new DcCacheService(DcA, dcaCache),
                new DcCacheService(DcB, _dcbCache)
            },
            NullLogger<CompositeRedisCacheService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _mux.GetDatabase(0).ExecuteAsync("FLUSHDB");
        await _mux.GetDatabase(1).ExecuteAsync("FLUSHDB");
        _mux.Dispose();

        await _mongoDb.Database.Client.DropDatabaseAsync(_dbName);
    }

    // ----- helpers -----

    private FeatureFlag CreateFlag(string key, bool isEnabled)
    {
        var enabledVariationId = Guid.NewGuid().ToString();
        var disabledVariationId = Guid.NewGuid().ToString();

        var variations = new List<Variation>
        {
            new() { Id = enabledVariationId, Name = "true", Value = "true" },
            new() { Id = disabledVariationId, Name = "false", Value = "false" }
        };

        return new FeatureFlag(
            envId: _envId,
            name: key,
            description: string.Empty,
            key: key,
            isEnabled: isEnabled,
            variationType: "boolean",
            variations: variations,
            disabledVariationId: disabledVariationId,
            enabledVariationId: enabledVariationId,
            tags: [],
            currentUserId: Guid.NewGuid()
        );
    }

    private static long VersionTokenOf(FeatureFlag flag) =>
        new DateTimeOffset(flag.UpdatedAt).ToUnixTimeMilliseconds();

    private DcBackfiller CreateBackfiller(IMessageProducer producer)
    {
        var services = new ServiceCollection();
        services.AddTransient<IFeatureFlagService>(_ => _flagService);
        services.AddTransient<ISegmentService>(_ => _segmentService);
        var provider = services.BuildServiceProvider();

        return new DcBackfiller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _composite,
            producer,
            NullLogger<DcBackfiller>.Instance);
    }

    // ----- acceptance -----

    [Fact]
    public async Task GatedCommit_BackfillHoldingStaleSnapshot_CannotRevertFresherCommittedPointer()
    {
        const string key = "stale-snapshot-flag";

        // ---- the source of truth (Mongo) still reflects the OLDER ts1 value: this is the
        // "stale snapshot" DcBackfiller.GetAllCommittedAsync will read. ----
        var flagAtTs1 = CreateFlag(key, isEnabled: false);
        await _flagService.AddOneAsync(flagAtTs1);
        var ts1 = VersionTokenOf(flagAtTs1);

        // ---- dc-b's Redis is ALREADY at the fresher ts2 (simulates a racing normal commit that
        // landed on dc-b after the backfill's Mongo snapshot was taken but before its write for
        // ts1 arrived). ----
        var flagAtTs2 = CreateFlag(key, isEnabled: true);
        flagAtTs2.Id = flagAtTs1.Id;
        flagAtTs2.UpdatedAt = flagAtTs1.UpdatedAt.AddSeconds(1);
        var ts2 = VersionTokenOf(flagAtTs2);
        Assert.True(ts2 > ts1);

        await _dcbCache.StageFlagAsync(flagAtTs2, ts2);
        await _dcbCache.CommitFlagAsync(_envId, flagAtTs2.Id.ToString(), ts2);

        var pointerKey = RedisCaches.FlagCommittedPointer(flagAtTs2.Id);
        Assert.Equal(ts2, (long)(await _mux.GetDatabase(1).StringGetAsync(pointerKey)));

        // ---- the backfill runs, holding the STALE ts1 Mongo snapshot ----
        var backfiller = CreateBackfiller(new NoopMessageProducer());
        var flagCount = await backfiller.BackfillDcAsync(DcB, ConsistencyMode.GatedCommit);
        Assert.Equal(1, flagCount);

        // ACCEPTANCE: dc-b's committed pointer + index must still read ts2 — the stale backfill
        // write for ts1 must have been rejected by the only-advance guard, not reverted the DC.
        var pointerAfter = await _mux.GetDatabase(1).StringGetAsync(pointerKey);
        Assert.Equal(ts2, (long)pointerAfter);

        var indexScore = await _mux.GetDatabase(1)
            .SortedSetScoreAsync(RedisKeys.FlagIndex(_envId), flagAtTs2.Id.ToString());
        Assert.NotNull(indexScore);
        Assert.Equal(ts2, (long)indexScore!.Value);

        // the newer (ts2) staged version is still what's readable, not the stale ts1 one.
        Assert.True(await _dcbCache.HasStagedFlagAsync(flagAtTs2.Id, ts2));
    }

    [Fact]
    public async Task GatedCommit_BackfillHoldingStaleSnapshot_CannotRevertFresherCommittedSegmentPointer()
    {
        const string key = "stale-snapshot-segment";

        var segmentAtTs1 = new Domain.Segments.Segment(
            workspaceId: Guid.NewGuid(),
            envId: _envId,
            name: key,
            key: key,
            type: Domain.Segments.SegmentType.EnvironmentSpecific,
            scopes: [],
            included: ["alice"],
            excluded: [],
            rules: [],
            description: string.Empty
        );
        await _segmentService.AddOneAsync(segmentAtTs1);
        var envIds = await _segmentService.GetEnvironmentIdsAsync(segmentAtTs1);
        var ts1 = new DateTimeOffset(segmentAtTs1.UpdatedAt).ToUnixTimeMilliseconds();

        var segmentAtTs2 = new Domain.Segments.Segment(
            workspaceId: segmentAtTs1.WorkspaceId,
            envId: _envId,
            name: key,
            key: key,
            type: Domain.Segments.SegmentType.EnvironmentSpecific,
            scopes: [],
            included: ["alice", "bob"],
            excluded: [],
            rules: [],
            description: string.Empty
        )
        {
            Id = segmentAtTs1.Id,
            UpdatedAt = segmentAtTs1.UpdatedAt.AddSeconds(1)
        };
        var ts2 = new DateTimeOffset(segmentAtTs2.UpdatedAt).ToUnixTimeMilliseconds();
        Assert.True(ts2 > ts1);

        // dc-b is already at the fresher ts2 (raced ahead of the backfill's stale Mongo snapshot).
        await _dcbCache.StageSegmentAsync(segmentAtTs2, ts2);
        await _dcbCache.CommitSegmentAsync(envIds, segmentAtTs2.Id.ToString(), ts2);

        var pointerKey = RedisCaches.SegmentCommittedPointer(segmentAtTs2.Id);
        Assert.Equal(ts2, (long)(await _mux.GetDatabase(1).StringGetAsync(pointerKey)));

        var backfiller = CreateBackfiller(new NoopMessageProducer());
        var flagCount = await backfiller.BackfillDcAsync(DcB, ConsistencyMode.GatedCommit);
        Assert.Equal(0, flagCount); // no flags seeded in this test, only the segment

        // ACCEPTANCE: dc-b's segment pointer + index must still read ts2, not reverted to ts1.
        var pointerAfter = await _mux.GetDatabase(1).StringGetAsync(pointerKey);
        Assert.Equal(ts2, (long)pointerAfter);

        var indexScore = await _mux.GetDatabase(1)
            .SortedSetScoreAsync(RedisKeys.SegmentIndex(_envId), segmentAtTs2.Id.ToString());
        Assert.NotNull(indexScore);
        Assert.Equal(ts2, (long)indexScore!.Value);
    }

    [Fact]
    public async Task BestEffort_BackfillHoldingStaleSnapshot_CannotRevertFresherLegacyUpsert()
    {
        const string key = "stale-snapshot-besteffort-flag";

        // Mongo (the backfill's snapshot) still reflects the OLDER, disabled ts1 value.
        var flagAtTs1 = CreateFlag(key, isEnabled: false);
        await _flagService.AddOneAsync(flagAtTs1);
        var ts1 = VersionTokenOf(flagAtTs1);

        // dc-b's Redis already has the fresher, enabled ts2 value (raced ahead via a normal
        // BestEffort upsert that isn't gated on a snapshot).
        var flagAtTs2 = CreateFlag(key, isEnabled: true);
        flagAtTs2.Id = flagAtTs1.Id;
        flagAtTs2.UpdatedAt = flagAtTs1.UpdatedAt.AddSeconds(1);
        var ts2 = VersionTokenOf(flagAtTs2);
        Assert.True(ts2 > ts1);

        await _dcbCache.UpsertFlagAsync(flagAtTs2);

        var valueKey = RedisKeys.Flag(flagAtTs2.Id);
        var storedBefore = (string?)await _mux.GetDatabase(1).StringGetAsync(valueKey);
        Assert.Contains("\"isEnabled\":true", storedBefore);

        var backfiller = CreateBackfiller(new NoopMessageProducer());
        var flagCount = await backfiller.BackfillDcAsync(DcB, ConsistencyMode.BestEffort);
        Assert.Equal(1, flagCount);

        // ACCEPTANCE: the legacy key must still read the ts2 (enabled) content and the index score
        // must still be ts2 — the backfill's stale ts1 upsert must have been rejected.
        var storedAfter = (string?)await _mux.GetDatabase(1).StringGetAsync(valueKey);
        Assert.Contains("\"isEnabled\":true", storedAfter);

        var indexScore = await _mux.GetDatabase(1)
            .SortedSetScoreAsync(RedisKeys.FlagIndex(_envId), flagAtTs2.Id.ToString());
        Assert.NotNull(indexScore);
        Assert.Equal(ts2, (long)indexScore!.Value);
    }

    private sealed class NoopMessageProducer : IMessageProducer
    {
        public Task PublishAsync<TMessage>(string topic, TMessage message) where TMessage : class =>
            Task.CompletedTask;
    }

    private sealed class TestRedisClient(IConnectionMultiplexer connection, int db) : IRedisClient
    {
        public IConnectionMultiplexer Connection { get; } = connection;

        public IDatabase GetDatabase() => Connection.GetDatabase(db);
    }
}
