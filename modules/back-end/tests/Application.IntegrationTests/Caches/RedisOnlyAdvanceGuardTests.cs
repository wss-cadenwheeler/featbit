using Domain.FeatureFlags;
using Domain.Segments;
using Infrastructure.Caches.Redis;
using StackExchange.Redis;

namespace Application.IntegrationTests.Caches;

/// <summary>
/// Integration tests for the #89 only-advance guard: a DC cache backfill snapshots the committed
/// values once and then awaits per-item writes, so a NEWER commit can land on the same DC before the
/// backfill's write for an OLDER snapshot does. Without a guard, that stale write would revert the
/// fresher committed pointer/value. These tests exercise the guard directly against a real Redis:
///   - CommitFlagAsync / CommitSegmentAsync: a stale ts must not move the committed pointer or the
///     sorted-set index score; a newer ts must still advance both.
///   - UpsertFlagIfNewerAsync / UpsertSegmentIfNewerAsync: the targeted BestEffort legacy upsert used
///     by the backfiller, guarded by the index score as the version register.
///
/// Requires a real Redis instance on port 6385 (override via OA_REDIS):
///   docker run -d --rm -p 6385:6379 --name featbit-test-redis-6385 redis:7-alpine
/// xunit 2.x has no runtime Assert.Skip, so an unreachable Redis fails loudly instead of silently
/// passing.
/// </summary>
[Trait("Category", "Integration")]
public class RedisOnlyAdvanceGuardTests : IAsyncLifetime
{
    private const string DefaultConnection = "localhost:6385";

    private ConnectionMultiplexer? _mux;
    private RedisCacheService? _sut;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("OA_REDIS") ?? DefaultConnection;

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
                "docker run -d --rm -p 6385:6379 --name featbit-test-redis-6385 redis:7-alpine " +
                "(or set the OA_REDIS env var).");
        }

        _sut = new RedisCacheService(new TestRedisClient(_mux));
    }

    public Task DisposeAsync()
    {
        _mux?.Dispose();
        return Task.CompletedTask;
    }

    // ----- CommitFlagAsync: pointer + index only-advance -----

    [Fact]
    public async Task CommitFlagAsync_StaleTs_DoesNotRevertPointerOrIndex()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var flagId = Guid.NewGuid();
        var flagIdString = flagId.ToString();

        var committedKey = RedisCaches.FlagCommittedPointer(flagId);
        var indexKey = RedisKeys.FlagIndex(envId);

        try
        {
            // A normal commit lands ts2 first (simulates the racing normal coordinator commit that
            // gets ahead of a backfill still holding an older snapshot).
            var ts2 = 2_000L;
            await sut.CommitFlagAsync(envId, flagIdString, ts2);

            Assert.Equal(ts2.ToString(), (string?)await db.StringGetAsync(committedKey));
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, flagIdString));

            // The backfill's delayed write for its stale ts1 snapshot arrives AFTER: must be a no-op.
            var ts1 = 1_000L;
            await sut.CommitFlagAsync(envId, flagIdString, ts1);

            // ACCEPTANCE: pointer + index must still read ts2, NOT reverted to ts1.
            Assert.Equal(ts2.ToString(), (string?)await db.StringGetAsync(committedKey));
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, flagIdString));
        }
        finally
        {
            await db.KeyDeleteAsync(committedKey);
            await db.SortedSetRemoveAsync(indexKey, flagIdString);
        }
    }

    [Fact]
    public async Task CommitFlagAsync_NewerTs_StillAdvancesPointerAndIndex()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var flagId = Guid.NewGuid();
        var flagIdString = flagId.ToString();

        var committedKey = RedisCaches.FlagCommittedPointer(flagId);
        var indexKey = RedisKeys.FlagIndex(envId);

        try
        {
            var ts1 = 1_000L;
            await sut.CommitFlagAsync(envId, flagIdString, ts1);
            Assert.Equal(ts1.ToString(), (string?)await db.StringGetAsync(committedKey));

            var ts2 = 2_000L;
            await sut.CommitFlagAsync(envId, flagIdString, ts2);

            // ACCEPTANCE: a genuinely newer ts still advances both pointer and index (the guard
            // must not be a no-op for the normal, monotonically-increasing path).
            Assert.Equal(ts2.ToString(), (string?)await db.StringGetAsync(committedKey));
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, flagIdString));
        }
        finally
        {
            await db.KeyDeleteAsync(committedKey);
            await db.SortedSetRemoveAsync(indexKey, flagIdString);
        }
    }

    // ----- CommitSegmentAsync: pointer + index only-advance (segment counterpart) -----

    [Fact]
    public async Task CommitSegmentAsync_StaleTs_DoesNotRevertPointerOrIndex()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var segmentIdString = segmentId.ToString();
        var envIds = new[] { envId };

        var committedKey = RedisCaches.SegmentCommittedPointer(segmentId);
        var indexKey = RedisKeys.SegmentIndex(envId);

        try
        {
            var ts2 = 2_000L;
            await sut.CommitSegmentAsync(envIds, segmentIdString, ts2);
            Assert.Equal(ts2.ToString(), (string?)await db.StringGetAsync(committedKey));

            var ts1 = 1_000L;
            await sut.CommitSegmentAsync(envIds, segmentIdString, ts1);

            Assert.Equal(ts2.ToString(), (string?)await db.StringGetAsync(committedKey));
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, segmentIdString));
        }
        finally
        {
            await db.KeyDeleteAsync(committedKey);
            await db.SortedSetRemoveAsync(indexKey, segmentIdString);
        }
    }

    [Fact]
    public async Task CommitSegmentAsync_NewerTs_StillAdvancesPointerAndIndex()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var segmentIdString = segmentId.ToString();
        var envIds = new[] { envId };

        var committedKey = RedisCaches.SegmentCommittedPointer(segmentId);
        var indexKey = RedisKeys.SegmentIndex(envId);

        try
        {
            var ts1 = 1_000L;
            await sut.CommitSegmentAsync(envIds, segmentIdString, ts1);

            var ts2 = 2_000L;
            await sut.CommitSegmentAsync(envIds, segmentIdString, ts2);

            Assert.Equal(ts2.ToString(), (string?)await db.StringGetAsync(committedKey));
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, segmentIdString));
        }
        finally
        {
            await db.KeyDeleteAsync(committedKey);
            await db.SortedSetRemoveAsync(indexKey, segmentIdString);
        }
    }

    // ----- UpsertFlagIfNewerAsync: targeted BestEffort legacy upsert guarded by index score -----

    [Fact]
    public async Task UpsertFlagIfNewerAsync_StaleValue_DoesNotRevertLegacyKeyOrIndex()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var flagId = Guid.NewGuid();
        var flagIdString = flagId.ToString();

        var valueKey = RedisKeys.Flag(flagId);
        var indexKey = RedisKeys.FlagIndex(envId);

        try
        {
            // A normal (unguarded) upsert lands the fresher value first — simulates a normal write
            // racing ahead of a backfill's stale snapshot.
            var ts2 = 2_000L;
            var flagAtTs2 = NewFlag(envId, flagId, isEnabled: true, ts2);
            await sut.UpsertFlagAsync(flagAtTs2);
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, flagIdString));

            // The backfiller's targeted, guarded write for its stale ts1 snapshot must be a no-op.
            var ts1 = 1_000L;
            var flagAtTs1 = NewFlag(envId, flagId, isEnabled: false, ts1);
            await sut.UpsertFlagIfNewerAsync(flagAtTs1);

            // ACCEPTANCE: the legacy value key must still read the ts2 (enabled) content, and the
            // index score must still be ts2 — the stale ts1 write must not have landed.
            var storedJson = (string?)await db.StringGetAsync(valueKey);
            Assert.Contains("\"isEnabled\":true", storedJson);
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, flagIdString));
        }
        finally
        {
            await db.KeyDeleteAsync(valueKey);
            await db.SortedSetRemoveAsync(indexKey, flagIdString);
        }
    }

    [Fact]
    public async Task UpsertFlagIfNewerAsync_NewerValue_StillAdvancesLegacyKeyAndIndex()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var flagId = Guid.NewGuid();
        var flagIdString = flagId.ToString();

        var valueKey = RedisKeys.Flag(flagId);
        var indexKey = RedisKeys.FlagIndex(envId);

        try
        {
            var ts1 = 1_000L;
            var flagAtTs1 = NewFlag(envId, flagId, isEnabled: false, ts1);
            await sut.UpsertFlagIfNewerAsync(flagAtTs1);

            var ts2 = 2_000L;
            var flagAtTs2 = NewFlag(envId, flagId, isEnabled: true, ts2);
            await sut.UpsertFlagIfNewerAsync(flagAtTs2);

            var storedJson = (string?)await db.StringGetAsync(valueKey);
            Assert.Contains("\"isEnabled\":true", storedJson);
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, flagIdString));
        }
        finally
        {
            await db.KeyDeleteAsync(valueKey);
            await db.SortedSetRemoveAsync(indexKey, flagIdString);
        }
    }

    // ----- UpsertSegmentIfNewerAsync: targeted BestEffort legacy upsert (segment counterpart) -----

    [Fact]
    public async Task UpsertSegmentIfNewerAsync_StaleValue_DoesNotRevertLegacyKeyOrIndex()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var segmentIdString = segmentId.ToString();
        var envIds = new[] { envId };

        var valueKey = RedisKeys.Segment(segmentId);
        var indexKey = RedisKeys.SegmentIndex(envId);

        try
        {
            var ts2 = 2_000L;
            var segmentAtTs2 = NewSegment(envId, segmentId, included: ["alice", "bob"], ts2);
            await sut.UpsertSegmentAsync(envIds, segmentAtTs2);
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, segmentIdString));

            var ts1 = 1_000L;
            var segmentAtTs1 = NewSegment(envId, segmentId, included: ["alice"], ts1);
            await sut.UpsertSegmentIfNewerAsync(envIds, segmentAtTs1);

            // ACCEPTANCE: the legacy value + index must still reflect ts2, not reverted to ts1.
            var storedJson = (string?)await db.StringGetAsync(valueKey);
            Assert.Contains("bob", storedJson);
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, segmentIdString));
        }
        finally
        {
            await db.KeyDeleteAsync(valueKey);
            await db.SortedSetRemoveAsync(indexKey, segmentIdString);
        }
    }

    [Fact]
    public async Task UpsertSegmentIfNewerAsync_NewerValue_StillAdvancesLegacyKeyAndIndex()
    {
        var sut = _sut!;
        var db = _mux!.GetDatabase();

        var envId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var segmentIdString = segmentId.ToString();
        var envIds = new[] { envId };

        var valueKey = RedisKeys.Segment(segmentId);
        var indexKey = RedisKeys.SegmentIndex(envId);

        try
        {
            var ts1 = 1_000L;
            var segmentAtTs1 = NewSegment(envId, segmentId, included: ["alice"], ts1);
            await sut.UpsertSegmentIfNewerAsync(envIds, segmentAtTs1);

            var ts2 = 2_000L;
            var segmentAtTs2 = NewSegment(envId, segmentId, included: ["alice", "bob"], ts2);
            await sut.UpsertSegmentIfNewerAsync(envIds, segmentAtTs2);

            var storedJson = (string?)await db.StringGetAsync(valueKey);
            Assert.Contains("bob", storedJson);
            Assert.Equal(ts2, await db.SortedSetScoreAsync(indexKey, segmentIdString));
        }
        finally
        {
            await db.KeyDeleteAsync(valueKey);
            await db.SortedSetRemoveAsync(indexKey, segmentIdString);
        }
    }

    private static FeatureFlag NewFlag(Guid envId, Guid flagId, bool isEnabled, long ts)
    {
        var enabledVariationId = Guid.NewGuid().ToString();
        var disabledVariationId = Guid.NewGuid().ToString();

        var flag = new FeatureFlag(
            envId: envId,
            name: "test-flag",
            description: string.Empty,
            key: "test-flag",
            isEnabled: isEnabled,
            variationType: "boolean",
            variations:
            [
                new Variation { Id = enabledVariationId, Name = "true", Value = "true" },
                new Variation { Id = disabledVariationId, Name = "false", Value = "false" }
            ],
            disabledVariationId: disabledVariationId,
            enabledVariationId: enabledVariationId,
            tags: [],
            currentUserId: Guid.NewGuid())
        {
            Id = flagId,
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
        };

        return flag;
    }

    private static Segment NewSegment(Guid envId, Guid segmentId, string[] included, long ts)
    {
        var segment = new Segment(
            workspaceId: Guid.NewGuid(),
            envId: envId,
            name: "test-segment",
            key: "test-segment",
            type: SegmentType.EnvironmentSpecific,
            scopes: [],
            included: included,
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
