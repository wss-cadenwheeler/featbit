using Api.Application.ControlPlane;
using Api.Infrastructure.Caches;
using Application.Caches;
using Application.ControlPlane;
using Application.Services;
using Domain.ControlPlane;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Segments;
using Infrastructure.Caches.Redis;
using Infrastructure.Persistence.MongoDb;
using Infrastructure.Services.MongoDb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using StackExchange.Redis;
using Action = Domain.Messages.Action;

namespace Api.IntegrationTests.ControlPlane;

/// <summary>
/// E1 returning-DC recovery acceptance tests. Exercises the locked Model A design end-to-end against
/// real infrastructure: a real MongoDB (committed flags + DC leases) and a real Redis whose two
/// logical DB indexes simulate two DCs' Redis (dc-a = db 0, dc-b = db 1).
///
/// Scenario: both DCs committed v1; dc-b loses its lease; the flag is committed to v2 on dc-a only
/// (dc-b absent); dc-b's lease returns -> a recovery tick backfills dc-b's Redis so it reaches v2
/// (versioned value key + committed pointer + index all at v2). A DC already current is a no-op.
///
/// Requires:
///  - MongoDB at mongodb://admin:password@localhost:27017 (unique throwaway DB, dropped on dispose).
///  - Redis on port 6387 (override via E1_REDIS):
///      docker run -d --rm -p 6387:6379 --name cp-e1-recovery-redis redis:7-alpine
/// Fails loudly (not silently skips) if either is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RecoveryWorkerTests : IAsyncLifetime
{
    private const string MongoConnectionString = "mongodb://admin:password@localhost:27017/?authSource=admin";
    private const string DefaultRedis = "localhost:6387";

    private const string DcA = "dc-a";
    private const string DcB = "dc-b";

    private readonly string _dbName = $"featbit_e1_test_{Guid.NewGuid():N}";
    private readonly Guid _envId = Guid.NewGuid();

    private MongoDbClient _mongoDb = null!;
    private FeatureFlagService _flagService = null!;
    private SegmentService _segmentService = null!;
    private MongoLeaseStore _leaseStore = null!;

    private ConnectionMultiplexer _mux = null!;
    private RedisCacheService _dcaCache = null!; // db 0
    private RedisCacheService _dcbCache = null!; // db 1
    private CompositeRedisCacheService _composite = null!;

    // Spy producer the SUT publishes the per-DC client-refresh command to. Set by CreateSut.
    private RecordingMessageProducer _producer = null!;

    private static string RedisConnectionString =>
        Environment.GetEnvironmentVariable("E1_REDIS") ?? DefaultRedis;

    public async Task InitializeAsync()
    {
        // ---- Mongo ----
        var options = Options.Create(new MongoDbOptions
        {
            ConnectionString = MongoConnectionString,
            Database = _dbName
        });
        _mongoDb = new MongoDbClient(options);
        await _mongoDb.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));

        _flagService = new FeatureFlagService(_mongoDb);
        _segmentService = new SegmentService(_mongoDb, NullLogger<SegmentService>.Instance);
        _leaseStore = new MongoLeaseStore(_mongoDb);

        // ---- Redis (two DB indexes = two DCs) ----
        var redisOptions = ConfigurationOptions.Parse(RedisConnectionString);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectTimeout = 2000;

        _mux = await ConnectionMultiplexer.ConnectAsync(redisOptions);
        if (!_mux.IsConnected)
        {
            throw new InvalidOperationException(
                $"No Redis reachable at '{RedisConnectionString}'. Start one with: " +
                "docker run -d --rm -p 6387:6379 --name cp-e1-recovery-redis redis:7-alpine " +
                "(or set the E1_REDIS env var).");
        }

        _dcaCache = new RedisCacheService(new TestRedisClient(_mux, db: 0));
        _dcbCache = new RedisCacheService(new TestRedisClient(_mux, db: 1));

        _composite = new CompositeRedisCacheService(
            new[]
            {
                new DcCacheService(DcA, _dcaCache),
                new DcCacheService(DcB, _dcbCache)
            },
            NullLogger<CompositeRedisCacheService>.Instance);
    }

    public async Task DisposeAsync()
    {
        // flush both DC DB indexes so a shared Redis is left clean
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

    private Segment CreateSegment(string key, string[] included)
    {
        // Environment-specific so GetEnvironmentIdsAsync resolves to [_envId] without any Mongo
        // env/project/org lookup — the env ids backfilled to the returning DC's index are deterministic.
        return new Segment(
            workspaceId: Guid.NewGuid(),
            envId: _envId,
            name: key,
            key: key,
            type: SegmentType.EnvironmentSpecific,
            scopes: [],
            included: included,
            excluded: [],
            rules: [],
            description: string.Empty
        );
    }

    private async Task UpsertLeaseAsync(string dcId, DateTimeOffset expiresAt)
    {
        await _leaseStore.UpsertLeaseAsync(new DcLease
        {
            DcId = dcId,
            Region = dcId,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            LeaseExpiresAt = expiresAt
        });
    }

    private RecoveryWorker CreateSut(ILogger<RecoveryWorker>? logger = null)
    {
        // Wire IFeatureFlagService + ILeaseStore through a real DI scope, matching how the worker
        // resolves them at runtime via IServiceScopeFactory.
        var services = new ServiceCollection();
        services.AddTransient<IFeatureFlagService>(_ => _flagService);
        services.AddTransient<ISegmentService>(_ => _segmentService);
        services.AddTransient<ILeaseStore>(_ => _leaseStore);
        var provider = services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:ConsistencyMode"] = "GatedCommit"
            })
            .Build();

        _producer = new RecordingMessageProducer();

        return new RecoveryWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _composite,
            _producer,
            configuration,
            logger ?? NullLogger<RecoveryWorker>.Instance);
    }

    /// <summary>
    /// The committed version token the worker derives for a flag (mirrors
    /// FeatureFlagChangeMessageHandler / RecoveryWorker: unix-ms of UpdatedAt).
    /// </summary>
    private static long VersionTokenOf(FeatureFlag flag) =>
        new DateTimeOffset(flag.UpdatedAt).ToUnixTimeMilliseconds();

    /// <summary>Segment counterpart of <see cref="VersionTokenOf(FeatureFlag)"/>.</summary>
    private static long VersionTokenOf(Segment segment) =>
        new DateTimeOffset(segment.UpdatedAt).ToUnixTimeMilliseconds();

    // ----- acceptance -----

    [Fact]
    public async Task Backfills_ReturningDc_To_LatestCommittedVersion()
    {
        const string key = "returning-dc";

        // ---- v1 committed on BOTH DCs (both live) ----
        var v1 = CreateFlag(key, isEnabled: false);
        v1.CommittedVersion = 1;
        await _flagService.AddOneAsync(v1);
        var v1Ts = VersionTokenOf(v1);

        await _dcaCache.StageFlagAsync(v1, v1Ts);
        await _dcaCache.CommitFlagAsync(_envId, v1.Id.ToString(), v1Ts);
        await _dcbCache.StageFlagAsync(v1, v1Ts);
        await _dcbCache.CommitFlagAsync(_envId, v1.Id.ToString(), v1Ts);

        var now = DateTimeOffset.UtcNow;
        await UpsertLeaseAsync(DcA, now.AddMinutes(5));
        await UpsertLeaseAsync(DcB, now.AddMinutes(5));

        var sut = CreateSut();

        // First tick: both DCs first-seen -> backfilled (harmless), establishes the watermark.
        await sut.RunOnceAsync();

        // ---- dc-b loses its lease ----
        await UpsertLeaseAsync(DcB, now.AddMinutes(-5)); // expired

        // ---- flag changes to v2, committed on dc-a ONLY (dc-b absent) ----
        // The committed top-level value advances to v2 with NO pending (mirrors the coordinator
        // committing on the live set while dc-b is evicted).
        var v2 = CreateFlag(key, isEnabled: true);
        v2.Id = v1.Id;
        v2.CommittedVersion = 2;
        // ensure a distinct, newer version token than v1
        v2.UpdatedAt = v1.UpdatedAt.AddSeconds(1);
        await _flagService.UpdateAsync(v2);
        var v2Ts = VersionTokenOf(v2);
        Assert.NotEqual(v1Ts, v2Ts);

        await _dcaCache.StageFlagAsync(v2, v2Ts);
        await _dcaCache.CommitFlagAsync(_envId, v2.Id.ToString(), v2Ts);

        // dc-b is still at v1 and never got v2.
        Assert.False(await _dcbCache.HasStagedFlagAsync(v2.Id, v2Ts));
        var dcbPointerBefore = await _mux.GetDatabase(1).StringGetAsync(RedisCaches.FlagCommittedPointer(v2.Id));
        Assert.Equal(v1Ts, (long)dcbPointerBefore);

        // A recovery tick now sees only dc-a live; nothing returned.
        var noReturn = await sut.RunOnceAsync();
        Assert.Equal(0, noReturn);

        // ---- dc-b's lease returns ----
        await UpsertLeaseAsync(DcB, now.AddMinutes(5));

        var backfilled = await sut.RunOnceAsync();

        // exactly one DC (dc-b) backfilled
        Assert.Equal(1, backfilled);

        // dc-b's Redis now has v2: versioned value key + committed pointer + index all at v2.
        Assert.True(await _dcbCache.HasStagedFlagAsync(v2.Id, v2Ts));

        var dcbPointerAfter = await _mux.GetDatabase(1).StringGetAsync(RedisCaches.FlagCommittedPointer(v2.Id));
        Assert.Equal(v2Ts, (long)dcbPointerAfter);

        var dcbIndexScore = await _mux.GetDatabase(1)
            .SortedSetScoreAsync(RedisKeys.FlagIndex(_envId), v2.Id.ToString());
        Assert.NotNull(dcbIndexScore);
        Assert.Equal(v2Ts, (long)dcbIndexScore!.Value);

        // dc-a was untouched by the recovery (it was already live, not returning).
        var dcaPointer = await _mux.GetDatabase(0).StringGetAsync(RedisCaches.FlagCommittedPointer(v2.Id));
        Assert.Equal(v2Ts, (long)dcaPointer);
    }

    [Fact]
    public async Task Backfills_ReturningDc_With_LatestCommittedSegmentVersion()
    {
        const string flagKey = "returning-dc-flag";
        const string segmentKey = "returning-dc-segment";

        // ---- a flag at v1 on BOTH DCs so the existing flag backfill path is exercised alongside ----
        var flag = CreateFlag(flagKey, isEnabled: false);
        flag.CommittedVersion = 1;
        await _flagService.AddOneAsync(flag);
        var flagTs = VersionTokenOf(flag);
        await _dcaCache.StageFlagAsync(flag, flagTs);
        await _dcaCache.CommitFlagAsync(_envId, flag.Id.ToString(), flagTs);
        await _dcbCache.StageFlagAsync(flag, flagTs);
        await _dcbCache.CommitFlagAsync(_envId, flag.Id.ToString(), flagTs);

        // ---- segment v1 committed on BOTH DCs (both live) ----
        var s1 = CreateSegment(segmentKey, included: ["alice"]);
        s1.CommittedVersion = 1;
        await _segmentService.AddOneAsync(s1);
        var s1Ts = VersionTokenOf(s1);
        var envIds = await _segmentService.GetEnvironmentIdsAsync(s1);
        Assert.Equal(new[] { _envId }, envIds);

        await _dcaCache.StageSegmentAsync(s1, s1Ts);
        await _dcaCache.CommitSegmentAsync(envIds, s1.Id.ToString(), s1Ts);
        await _dcbCache.StageSegmentAsync(s1, s1Ts);
        await _dcbCache.CommitSegmentAsync(envIds, s1.Id.ToString(), s1Ts);

        var now = DateTimeOffset.UtcNow;
        await UpsertLeaseAsync(DcA, now.AddMinutes(5));
        await UpsertLeaseAsync(DcB, now.AddMinutes(5));

        var sut = CreateSut();

        // First tick: both DCs first-seen -> backfilled (harmless), establishes the watermark.
        await sut.RunOnceAsync();

        // ---- dc-b loses its lease ----
        await UpsertLeaseAsync(DcB, now.AddMinutes(-5)); // expired

        // ---- segment changes to v2, committed on dc-a ONLY (dc-b absent) ----
        var s2 = CreateSegment(segmentKey, included: ["alice", "bob"]);
        s2.Id = s1.Id;
        s2.CommittedVersion = 2;
        // ensure a distinct, newer version token than v1
        s2.UpdatedAt = s1.UpdatedAt.AddSeconds(1);
        await _segmentService.UpdateAsync(s2);
        var s2Ts = VersionTokenOf(s2);
        Assert.NotEqual(s1Ts, s2Ts);

        await _dcaCache.StageSegmentAsync(s2, s2Ts);
        await _dcaCache.CommitSegmentAsync(envIds, s2.Id.ToString(), s2Ts);

        // dc-b is still at v1 and never got v2.
        Assert.False(await _dcbCache.HasStagedSegmentAsync(s2.Id, s2Ts));
        var dcbPointerBefore =
            await _mux.GetDatabase(1).StringGetAsync(RedisCaches.SegmentCommittedPointer(s2.Id));
        Assert.Equal(s1Ts, (long)dcbPointerBefore);

        // A recovery tick now sees only dc-a live; nothing returned.
        var noReturn = await sut.RunOnceAsync();
        Assert.Equal(0, noReturn);

        // ---- dc-b's lease returns ----
        await UpsertLeaseAsync(DcB, now.AddMinutes(5));

        var backfilled = await sut.RunOnceAsync();

        // exactly one DC (dc-b) backfilled
        Assert.Equal(1, backfilled);

        // dc-b's Redis now has the segment at v2: versioned value key + committed pointer + index.
        Assert.True(await _dcbCache.HasStagedSegmentAsync(s2.Id, s2Ts));

        var dcbPointerAfter =
            await _mux.GetDatabase(1).StringGetAsync(RedisCaches.SegmentCommittedPointer(s2.Id));
        Assert.Equal(s2Ts, (long)dcbPointerAfter);

        var dcbIndexScore = await _mux.GetDatabase(1)
            .SortedSetScoreAsync(RedisKeys.SegmentIndex(_envId), s2.Id.ToString());
        Assert.NotNull(dcbIndexScore);
        Assert.Equal(s2Ts, (long)dcbIndexScore!.Value);

        // dc-a was untouched by the recovery (it was already live, not returning).
        var dcaPointer =
            await _mux.GetDatabase(0).StringGetAsync(RedisCaches.SegmentCommittedPointer(s2.Id));
        Assert.Equal(s2Ts, (long)dcaPointer);

        // the flag backfill still works (don't regress): dc-b has the flag committed at v1.
        Assert.True(await _dcbCache.HasStagedFlagAsync(flag.Id, flagTs));
        var dcbFlagPointer =
            await _mux.GetDatabase(1).StringGetAsync(RedisCaches.FlagCommittedPointer(flag.Id));
        Assert.Equal(flagTs, (long)dcbFlagPointer);
    }

    [Fact]
    public async Task NoOp_When_NoDcReturned()
    {
        const string key = "steady-state";

        var v1 = CreateFlag(key, isEnabled: true);
        v1.CommittedVersion = 1;
        await _flagService.AddOneAsync(v1);
        var v1Ts = VersionTokenOf(v1);

        await _dcaCache.StageFlagAsync(v1, v1Ts);
        await _dcaCache.CommitFlagAsync(_envId, v1.Id.ToString(), v1Ts);
        await _dcbCache.StageFlagAsync(v1, v1Ts);
        await _dcbCache.CommitFlagAsync(_envId, v1.Id.ToString(), v1Ts);

        var now = DateTimeOffset.UtcNow;
        await UpsertLeaseAsync(DcA, now.AddMinutes(5));
        await UpsertLeaseAsync(DcB, now.AddMinutes(5));

        var sut = CreateSut();

        // First tick establishes the watermark (both first-seen -> backfilled once).
        var first = await sut.RunOnceAsync();
        Assert.Equal(2, first);

        // Second tick with the SAME live set: nothing returned -> no-op.
        var second = await sut.RunOnceAsync();
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task Publishes_Targeted_ClientRefresh_For_ReturningDc()
    {
        const string key = "returning-dc-refresh";

        // ---- v1 committed on BOTH DCs (both live) ----
        var v1 = CreateFlag(key, isEnabled: false);
        v1.CommittedVersion = 1;
        await _flagService.AddOneAsync(v1);
        var v1Ts = VersionTokenOf(v1);

        await _dcaCache.StageFlagAsync(v1, v1Ts);
        await _dcaCache.CommitFlagAsync(_envId, v1.Id.ToString(), v1Ts);
        await _dcbCache.StageFlagAsync(v1, v1Ts);
        await _dcbCache.CommitFlagAsync(_envId, v1.Id.ToString(), v1Ts);

        var now = DateTimeOffset.UtcNow;
        await UpsertLeaseAsync(DcA, now.AddMinutes(5));
        await UpsertLeaseAsync(DcB, now.AddMinutes(5));

        var sut = CreateSut();

        // First tick: both DCs first-seen -> both backfilled -> a targeted command per DC.
        await sut.RunOnceAsync();

        var firstTickCommands = _producer.Published
            .Where(p => p.Topic == ControlPlaneTopics.ControlPlaneCommand)
            .Select(p => (ControlPlaneCommand)p.Message)
            .ToList();
        Assert.Equal(2, firstTickCommands.Count);
        Assert.All(firstTickCommands, c => Assert.Equal(Action.PushFullSync, c.Action));
        Assert.Contains(firstTickCommands, c => c.TargetDcId == DcA);
        Assert.Contains(firstTickCommands, c => c.TargetDcId == DcB);

        // ---- steady state: same live set -> nothing returned -> NO new commands published ----
        var publishedBefore = _producer.Published.Count;
        var noReturn = await sut.RunOnceAsync();
        Assert.Equal(0, noReturn);
        Assert.Equal(publishedBefore, _producer.Published.Count);

        // ---- dc-b leaves then returns -> exactly one targeted command, for dc-b only ----
        await UpsertLeaseAsync(DcB, now.AddMinutes(-5)); // expired
        await sut.RunOnceAsync();                        // dc-b drops out of the live set (no return)
        var beforeReturn = _producer.Published.Count;

        await UpsertLeaseAsync(DcB, now.AddMinutes(5));  // dc-b returns
        var backfilled = await sut.RunOnceAsync();
        Assert.Equal(1, backfilled);

        var returnCommands = _producer.Published
            .Skip(beforeReturn)
            .Where(p => p.Topic == ControlPlaneTopics.ControlPlaneCommand)
            .Select(p => (ControlPlaneCommand)p.Message)
            .ToList();
        var refresh = Assert.Single(returnCommands);
        Assert.Equal(Action.PushFullSync, refresh.Action);
        Assert.Equal(DcB, refresh.TargetDcId);
    }

    // ----- test doubles -----

    /// <summary>
    /// Spy <see cref="IMessageProducer"/> that records every (topic, message) published, so a test can
    /// assert the per-DC client-refresh <see cref="ControlPlaneCommand"/> was published after backfill.
    /// </summary>
    private sealed class RecordingMessageProducer : IMessageProducer
    {
        public List<(string Topic, object Message)> Published { get; } = new();

        public Task PublishAsync<TMessage>(string topic, TMessage message) where TMessage : class
        {
            Published.Add((topic, message));
            return Task.CompletedTask;
        }
    }

    private sealed class TestRedisClient(IConnectionMultiplexer connection, int db) : IRedisClient
    {
        public IConnectionMultiplexer Connection { get; } = connection;

        public IDatabase GetDatabase() => Connection.GetDatabase(db);
    }
}
