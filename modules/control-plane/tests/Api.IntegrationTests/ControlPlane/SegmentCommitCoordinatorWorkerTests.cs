using Api.Application.ControlPlane;
using Api.Infrastructure.Caches;
using Application.Caches;
using Application.ControlPlane;
using Application.Segments;
using Application.Services;
using Domain.ControlPlane;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Segments;
using Infrastructure.Caches.Redis;
using Infrastructure.Persistence.MongoDb;
using Infrastructure.Services.MongoDb;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using StackExchange.Redis;

namespace Api.IntegrationTests.ControlPlane;

/// <summary>
/// S3 (#17) commit coordinator SEGMENT acceptance tests. Mirrors
/// <see cref="CommitCoordinatorWorkerTests"/> for the segment loop: a pending (staged-but-not-
/// committed) segment version is committed only once EVERY live DC has it staged. On commit, the
/// committed pointer/index is advanced in every DC's Redis, the Mongo pending is promoted
/// (version-guarded), and the affected-flags segment-change is published per env (replicating
/// SegmentChangeMessageHandler's BestEffort propagation, deferred to commit time).
///
/// Real infra (fails loudly, never silently skips):
///  - MongoDB at mongodb://admin:password@localhost:27017 (unique throwaway DB, dropped on dispose).
///  - Redis on port 6389 (override via S3_REDIS): two logical DB indexes simulate two DCs
///    (dc-a = db 0, dc-b = db 1).
///      docker run -d --rm -p 6389:6379 --name s3-redis redis:7-alpine
///
/// The segment publish is spied via a fake <see cref="ISegmentMessageService"/> so the tests assert
/// the per-env segment change fires ONLY on commit.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SegmentCommitCoordinatorWorkerTests : IAsyncLifetime
{
    private const string MongoConnectionString = "mongodb://admin:password@localhost:27017/?authSource=admin";
    private const string DefaultRedis = "localhost:6389";

    private const string DcA = "dc-a";
    private const string DcB = "dc-b";

    private readonly string _dbName = $"featbit_s3_test_{Guid.NewGuid():N}";
    private readonly Guid _envId = Guid.NewGuid();

    private MongoDbClient _mongoDb = null!;
    private SegmentService _segmentService = null!;
    private MongoLeaseStore _leaseStore = null!;

    private ConnectionMultiplexer _mux = null!;
    private RedisCacheService _dcaCache = null!; // db 0
    private RedisCacheService _dcbCache = null!; // db 1
    private CompositeRedisCacheService _composite = null!;

    private static string RedisConnectionString =>
        Environment.GetEnvironmentVariable("S3_REDIS") ?? DefaultRedis;

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
                "docker run -d --rm -p 6389:6379 --name s3-redis redis:7-alpine " +
                "(or set the S3_REDIS env var).");
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
        await _mux.GetDatabase(0).ExecuteAsync("FLUSHDB");
        await _mux.GetDatabase(1).ExecuteAsync("FLUSHDB");
        _mux.Dispose();

        await _mongoDb.Database.Client.DropDatabaseAsync(_dbName);
    }

    // ----- helpers -----

    private Segment CreateSegment(string key, params string[] included)
    {
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
            description: string.Empty);
    }

    /// <summary>
    /// Seeds a committed segment (v1, included=["alice"]) with a staged pending change
    /// (v2, included=["bob"]). The pending value carries the SAME Id and its
    /// <c>UpdatedAt</c> is set so its unix-ms equals the pending version, matching how S2 stages.
    /// </summary>
    private async Task<long> SeedCommittedV1PendingV2(string key)
    {
        var committed = CreateSegment(key, "alice");
        committed.CommittedVersion = 1;
        await _segmentService.AddOneAsync(committed);

        const long pendingVersion = 2;

        var pendingValue = CreateSegment(key, "bob");
        pendingValue.Id = committed.Id;
        pendingValue.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(pendingVersion).UtcDateTime;

        await _segmentService.SetPendingAsync(committed.Id, pendingValue, pendingVersion);

        return pendingVersion;
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

    private CommitCoordinatorWorker CreateSut(
        SpySegmentMessageService segmentMessages,
        ILogger<CommitCoordinatorWorker>? logger = null)
    {
        // Wire the same services the worker resolves per tick through a real DI scope. There are no
        // pending flags in these tests, so IFeatureFlagService is a real (empty) service. The
        // segment publish is spied via the fake ISegmentMessageService.
        var services = new ServiceCollection();
        services.AddTransient<IFeatureFlagService>(_ => new FeatureFlagService(_mongoDb));
        services.AddTransient<ISegmentService>(_ => _segmentService);
        services.AddTransient<ILeaseStore>(_ => _leaseStore);
        services.AddTransient<ISegmentMessageService>(_ => segmentMessages);
        services.AddTransient<IFeatureFlagAppService>(_ => new ThrowingFeatureFlagAppService());
        var provider = services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:ConsistencyMode"] = "GatedCommit"
            })
            .Build();

        return new CommitCoordinatorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _composite,
            new SpyMessageProducer(),
            configuration,
            logger ?? NullLogger<CommitCoordinatorWorker>.Instance);
    }

    // ----- acceptance cases -----

    [Fact]
    public async Task NoCommit_When_OnlyOneOfTwoLiveDcs_HasStaged()
    {
        const string key = "seg-only-one-dc-staged";
        var v = await SeedCommittedV1PendingV2(key);
        var segment = await _segmentService.GetAsync(await PendingId(key));

        var now = DateTimeOffset.UtcNow;
        await UpsertLeaseAsync(DcA, now.AddMinutes(5));
        await UpsertLeaseAsync(DcB, now.AddMinutes(5));

        // only dc-a (db 0) has v2 staged
        await _dcaCache.StageSegmentAsync(segment.Pending!.Value, v);

        var spy = new SpySegmentMessageService();
        var sut = CreateSut(spy);

        var committed = await sut.RunOnceAsync();

        Assert.Equal(0, committed);
        Assert.Empty(spy.Published);

        // committed read still returns the OLD value; pending intact
        var read = await _segmentService.GetCommittedAsync(segment.Id);
        Assert.Equal(1, read.CommittedVersion);
        Assert.Equal(new[] { "alice" }, read.Included);

        var raw = await _segmentService.GetAsync(segment.Id);
        Assert.NotNull(raw.Pending);
        Assert.Equal(v, raw.Pending!.Version);

        // dc-b committed pointer never advanced
        Assert.False(await _dcbCache.HasStagedSegmentAsync(segment.Id, v));
    }

    [Fact]
    public async Task Commits_When_AllLiveDcs_HaveStaged()
    {
        const string key = "seg-all-dcs-staged";
        var v = await SeedCommittedV1PendingV2(key);
        var segment = await _segmentService.GetAsync(await PendingId(key));

        var now = DateTimeOffset.UtcNow;
        await UpsertLeaseAsync(DcA, now.AddMinutes(5));
        await UpsertLeaseAsync(DcB, now.AddMinutes(5));

        // both DCs have v2 staged
        await _dcaCache.StageSegmentAsync(segment.Pending!.Value, v);
        await _dcbCache.StageSegmentAsync(segment.Pending!.Value, v);

        var spy = new SpySegmentMessageService();
        var sut = CreateSut(spy);

        var committed = await sut.RunOnceAsync();

        Assert.Equal(1, committed);

        // committed read now returns the NEW value + advanced version
        var read = await _segmentService.GetCommittedAsync(segment.Id);
        Assert.Equal(v, read.CommittedVersion);
        Assert.Equal(new[] { "bob" }, read.Included);

        // pending slot cleared
        var raw = await _segmentService.GetAsync(segment.Id);
        Assert.Null(raw.Pending);

        // committed pointer advanced in BOTH DCs (broadcast)
        var dcaPointer = await _mux.GetDatabase(0).StringGetAsync(RedisCaches.SegmentCommittedPointer(segment.Id));
        var dcbPointer = await _mux.GetDatabase(1).StringGetAsync(RedisCaches.SegmentCommittedPointer(segment.Id));
        Assert.Equal(v, (long)dcaPointer);
        Assert.Equal(v, (long)dcbPointer);

        // segment change published for the segment's single env (env-specific -> [EnvId])
        var publish = Assert.Single(spy.Published);
        Assert.Equal(_envId, publish.EnvId);
        Assert.Equal(segment.Id, publish.Segment.Id);
        Assert.Equal(new[] { "bob" }, publish.Segment.Included);
    }

    [Fact]
    public async Task Commits_Ignoring_ExpiredDc_When_OnlyLiveDc_HasStaged()
    {
        const string key = "seg-expired-dc-ignored";
        var v = await SeedCommittedV1PendingV2(key);
        var segment = await _segmentService.GetAsync(await PendingId(key));

        var now = DateTimeOffset.UtcNow;
        await UpsertLeaseAsync(DcA, now.AddMinutes(5));   // dc-a live
        await UpsertLeaseAsync(DcB, now.AddMinutes(-5));  // dc-b lease EXPIRED

        // only the live DC (dc-a) has v2 staged
        await _dcaCache.StageSegmentAsync(segment.Pending!.Value, v);

        var spy = new SpySegmentMessageService();
        var sut = CreateSut(spy);

        var committed = await sut.RunOnceAsync();

        Assert.Equal(1, committed);

        var read = await _segmentService.GetCommittedAsync(segment.Id);
        Assert.Equal(v, read.CommittedVersion);
        Assert.Single(spy.Published);
    }

    [Fact]
    public async Task Skips_When_PendingVersion_NotNewerThanCommitted()
    {
        // committed v5, pending staged at v2 (stale) -> must be skipped on monotonicity (#34)
        const string key = "seg-stale-pending";
        var committed = CreateSegment(key, "alice");
        committed.CommittedVersion = 5;
        await _segmentService.AddOneAsync(committed);

        var pendingValue = CreateSegment(key, "bob");
        pendingValue.Id = committed.Id;
        pendingValue.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(2).UtcDateTime;
        await _segmentService.SetPendingAsync(committed.Id, pendingValue, version: 2);

        var now = DateTimeOffset.UtcNow;
        await UpsertLeaseAsync(DcA, now.AddMinutes(5));
        await UpsertLeaseAsync(DcB, now.AddMinutes(5));

        // even if both DCs have it staged, the stale version must not be committed
        await _dcaCache.StageSegmentAsync(pendingValue, 2);
        await _dcbCache.StageSegmentAsync(pendingValue, 2);

        var spy = new SpySegmentMessageService();
        var sut = CreateSut(spy);

        var committed2 = await sut.RunOnceAsync();

        Assert.Equal(0, committed2);
        Assert.Empty(spy.Published);

        var read = await _segmentService.GetCommittedAsync(committed.Id);
        Assert.Equal(5, read.CommittedVersion);
        Assert.Equal(new[] { "alice" }, read.Included);
    }

    [Fact]
    public async Task NoCommit_When_NoLiveDcs()
    {
        const string key = "seg-no-live-dcs";
        var v = await SeedCommittedV1PendingV2(key);
        var segment = await _segmentService.GetAsync(await PendingId(key));

        // both DCs have it staged, but NO live leases exist
        await _dcaCache.StageSegmentAsync(segment.Pending!.Value, v);
        await _dcbCache.StageSegmentAsync(segment.Pending!.Value, v);

        var spy = new SpySegmentMessageService();
        var sut = CreateSut(spy);

        var committed = await sut.RunOnceAsync();

        Assert.Equal(0, committed);
        Assert.Empty(spy.Published);

        var read = await _segmentService.GetCommittedAsync(segment.Id);
        Assert.Equal(1, read.CommittedVersion);
    }

    [Fact]
    public async Task SecondTick_IsIdempotent_AfterCommit()
    {
        const string key = "seg-idempotent";
        var v = await SeedCommittedV1PendingV2(key);
        var segment = await _segmentService.GetAsync(await PendingId(key));

        var now = DateTimeOffset.UtcNow;
        await UpsertLeaseAsync(DcA, now.AddMinutes(5));
        await UpsertLeaseAsync(DcB, now.AddMinutes(5));

        await _dcaCache.StageSegmentAsync(segment.Pending!.Value, v);
        await _dcbCache.StageSegmentAsync(segment.Pending!.Value, v);

        var spy = new SpySegmentMessageService();
        var sut = CreateSut(spy);

        var first = await sut.RunOnceAsync();
        var second = await sut.RunOnceAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, second); // nothing left pending -> no-op
        Assert.Single(spy.Published);
    }

    // ----- eviction observability (#16) -----

    [Fact]
    public async Task EvictedCommit_Logs_And_IncrementsCounter_NamingEvictedDc()
    {
        const string key = "seg-evicted-commit-observed";
        var v = await SeedCommittedV1PendingV2(key);
        var segment = await _segmentService.GetAsync(await PendingId(key));

        var now = DateTimeOffset.UtcNow;
        await UpsertLeaseAsync(DcA, now.AddMinutes(5));   // dc-a live
        await UpsertLeaseAsync(DcB, now.AddMinutes(-5));  // dc-b lease EXPIRED -> evicted

        await _dcaCache.StageSegmentAsync(segment.Pending!.Value, v);

        var logger = new CapturingLogger();
        var spy = new SpySegmentMessageService();
        var sut = CreateSut(spy, logger);

        using var counter = new CounterCollector(CommitCoordinatorWorker.EvictedCommitCounterName);

        var committed = await sut.RunOnceAsync();

        Assert.Equal(1, committed);

        // metric: exactly one increment, tagged with the evicted dc_id
        var measurement = Assert.Single(counter.Measurements);
        Assert.Equal(1, measurement.Value);
        Assert.Equal(DcB, measurement.Tags["dc_id"]);

        // log: a warning naming the segment, the version, and the evicted DC
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(segment.Id.ToString(), warning);
        Assert.Contains(v.ToString(), warning);
        Assert.Contains(DcB, warning);
    }

    // ----- helpers -----

    /// <summary>Resolves the seeded segment's Id by its key (single env-specific segment per test).</summary>
    private async Task<Guid> PendingId(string key)
    {
        var pending = await _segmentService.GetPendingAsync();
        return pending.First(s => s.Key == key).Id;
    }

    // ----- test doubles -----

    private sealed class SpySegmentMessageService : ISegmentMessageService
    {
        public List<(Guid EnvId, ICollection<FlagReference> AffectedFlags, Segment Segment)> Published { get; } = new();

        public ValueTask<ICollection<FlagReference>> GetAffectedFlagsAsync(Guid envId, OnSegmentChange notification)
        {
            // No related flags are seeded in these tests, so there are no affected flags to compute.
            // Returning empty keeps OnSegmentUpdatedAsync (the throwing app service) from being hit.
            return ValueTask.FromResult<ICollection<FlagReference>>([]);
        }

        public Task PublishSegmentChangeMessage(Guid envId, ICollection<FlagReference> affectedFlags, Segment segment)
        {
            Published.Add((envId, affectedFlags, segment));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Guard: with no affected flags, OnSegmentUpdatedAsync must never be called. If the coordinator
    /// ever calls it (a regression), the test fails loudly instead of silently passing.
    /// </summary>
    private sealed class ThrowingFeatureFlagAppService : IFeatureFlagAppService
    {
        public Task ApplyDraftAsync(Guid draftId, string operation, Guid operatorId) =>
            throw new InvalidOperationException("ApplyDraftAsync should not be called by the commit coordinator.");

        public Task OnSegmentUpdatedAsync(Segment segment, Guid operatorId, ICollection<FlagReference> flagReferences) =>
            throw new InvalidOperationException(
                "OnSegmentUpdatedAsync should not be called when there are no affected flags.");
    }

    private sealed class CounterCollector : IDisposable
    {
        private readonly MeterListener _listener;

        public List<(long Value, IReadOnlyDictionary<string, object?> Tags)> Measurements { get; } = new();

        public CounterCollector(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CommitCoordinatorWorker.MeterName
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };

            _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            {
                var dict = new Dictionary<string, object?>();
                foreach (var tag in tags)
                {
                    dict[tag.Key] = tag.Value;
                }

                Measurements.Add((measurement, dict));
            });

            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class CapturingLogger : ILogger<CommitCoordinatorWorker>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class SpyMessageProducer : IMessageProducer
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
