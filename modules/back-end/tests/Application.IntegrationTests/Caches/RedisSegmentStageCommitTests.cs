using Domain.Segments;
using Infrastructure.Caches.Redis;
using StackExchange.Redis;

namespace Application.IntegrationTests.Caches;

/// <summary>
/// Integration tests for the B2 Redis segment stage/commit storage feature (the segment
/// equivalents of the B1 flag stage/commit storage).
///
/// These tests require a real Redis instance. The orchestrating issue (#9) spins one up on a
/// NON-default port (6381) because 6379/6380 are taken by other projects:
///   docker run -d --rm -p 6381:6379 --name b2-redis redis:7-alpine
///
/// The connection string can be overridden via the B2_REDIS env var. xunit 2.x has no runtime
/// Assert.Skip, so if Redis is unreachable the tests fail loudly with a clear message rather
/// than passing silently — the orchestrating issue requires verification against a real Redis.
/// </summary>
public class RedisSegmentStageCommitTests : IAsyncLifetime
{
    private const string DefaultConnection = "localhost:6381";

    private ConnectionMultiplexer? _mux;
    private RedisCacheService? _sut;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("B2_REDIS") ?? DefaultConnection;

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
                "docker run -d --rm -p 6381:6379 --name b2-redis redis:7-alpine " +
                "(or set the B2_REDIS env var).");
        }

        _sut = new RedisCacheService(new TestRedisClient(_mux));
    }

    public Task DisposeAsync()
    {
        _mux?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StageThenCommit_KeepsOldCommittedValueReadableUntilCommit()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var segmentIdString = segmentId.ToString();
        var envIds = new[] { envId };

        // baseline: commit v1 first so there is an existing committed pointer
        var v1 = 1_000L;
        var segmentV1 = NewSegment(envId, segmentId, v1);
        await sut.StageSegmentAsync(segmentV1, v1);
        await sut.CommitSegmentAsync(envIds, segmentIdString, v1);

        var committedKey = RedisCaches.SegmentCommittedPointer(segmentId);
        var indexKey = RedisKeys.SegmentIndex(envId);

        Assert.Equal(v1.ToString(), (string?)await db.StringGetAsync(committedKey));
        Assert.Equal(v1, await db.SortedSetScoreAsync(indexKey, segmentIdString));

        // stage v2: the versioned value key must be written, but the committed
        // pointer and the index score must remain pointing at v1.
        var v2 = 2_000L;
        var segmentV2 = NewSegment(envId, segmentId, v2);
        await sut.StageSegmentAsync(segmentV2, v2);

        Assert.True(await db.KeyExistsAsync(RedisCaches.SegmentVersion(segmentId, v2)),
            "staged versioned value key should exist");
        Assert.True(await db.KeyExistsAsync(RedisCaches.SegmentVersion(segmentId, v1)),
            "previously committed versioned value key should still exist");

        // ACCEPTANCE: after StageSegmentAsync the committed pointer/index are unchanged.
        Assert.Equal(v1.ToString(), (string?)await db.StringGetAsync(committedKey));
        Assert.Equal(v1, await db.SortedSetScoreAsync(indexKey, segmentIdString));

        // commit v2: pointer flips to v2 and index score advances to v2.
        await sut.CommitSegmentAsync(envIds, segmentIdString, v2);

        // ACCEPTANCE: after CommitSegmentAsync, pointer == ts and the index score advances.
        Assert.Equal(v2.ToString(), (string?)await db.StringGetAsync(committedKey));
        Assert.Equal(v2, await db.SortedSetScoreAsync(indexKey, segmentIdString));

        // cleanup
        await db.KeyDeleteAsync(committedKey);
        await db.KeyDeleteAsync(RedisCaches.SegmentVersion(segmentId, v1));
        await db.KeyDeleteAsync(RedisCaches.SegmentVersion(segmentId, v2));
        await db.SortedSetRemoveAsync(indexKey, segmentIdString);
    }

    [Fact]
    public async Task StageSegmentAsync_DoesNotTouchSortedSetIndex()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var segmentIdString = segmentId.ToString();
        var indexKey = RedisKeys.SegmentIndex(envId);

        var ts = 5_000L;
        var segment = NewSegment(envId, segmentId, ts);

        // No prior commit, so the index has no member for this segment.
        await sut.StageSegmentAsync(segment, ts);

        // staging must NOT add the segment to the env index nor move the committed pointer.
        Assert.False((await db.SortedSetScoreAsync(indexKey, segmentIdString)).HasValue,
            "staging must not write the sorted-set index");
        Assert.False(await db.KeyExistsAsync(RedisCaches.SegmentCommittedPointer(segmentId)),
            "staging must not write the committed pointer");

        // cleanup
        await db.KeyDeleteAsync(RedisCaches.SegmentVersion(segmentId, ts));
    }

    private static Segment NewSegment(Guid envId, Guid segmentId, long ts)
    {
        var segment = new Segment(
            workspaceId: Guid.NewGuid(),
            envId: envId,
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

        return segment;
    }

    private sealed class TestRedisClient(IConnectionMultiplexer connection) : IRedisClient
    {
        public IConnectionMultiplexer Connection { get; } = connection;

        public IDatabase GetDatabase() => Connection.GetDatabase();
    }
}
