using Domain.Segments;
using Domain.Targeting;
using Domain.Utils;
using Infrastructure.Persistence.EntityFrameworkCore;
using Infrastructure.Services.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Application.IntegrationTests.Segments;

/// <summary>
/// #72c acceptance: with the xmin concurrency token in place (#76), SegmentService's
/// SetPendingAsync/PromotePendingAsync survive a racing writer's DbUpdateConcurrencyException by
/// detaching the stale tracked entity and retrying — the version guards re-evaluate against the
/// fresh row, converging to the same semantics the Mongo provider gets from its version-filtered
/// UpdateOneAsync/ReplaceOneAsync: the loser of a race either no-ops (SetPendingAsync) or returns
/// false (PromotePendingAsync); it never throws and never clobbers the winner.
///
/// Integration test against a real Postgres instance (throwaway container on port 5436).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SegmentRetryOnConflictPostgresTests : IAsyncLifetime
{
    // The throwaway Postgres container is started on 5436 with database "featbit".
    private const string BaseConnectionString =
        "Host=localhost;Port=5436;Database=featbit;Username=postgres;Password=please_change_me";

    // Use a UNIQUE throwaway database per test instance so EnsureCreated/EnsureDeleted in
    // different tests do not race each other. EF creates and drops this database.
    private readonly string _dbName = $"featbit_72c_test_{Guid.NewGuid():N}";
    private NpgsqlDataSource _dataSource = null!;
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _envId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        // fail fast / readable skip if Postgres is not available. Probe the always-present
        // "postgres" maintenance database rather than the seed "featbit" database.
        var probeConnectionString = new NpgsqlConnectionStringBuilder(BaseConnectionString)
        {
            Database = "postgres"
        }.ConnectionString;
        await using (var probe = new NpgsqlConnection(probeConnectionString))
        {
            await probe.OpenAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(BaseConnectionString)
        {
            Database = _dbName
        }.ConnectionString;

        // Mirror the production data source: dynamic JSON is required for the jsonb POCO
        // columns (Rules/Pending), and the snake_case naming convention is required so
        // EnsureCreated materializes the expected columns (incl. the xmin token).
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder
            .EnableDynamicJson()
            .ConfigureJsonOptions(ReusableJsonSerializerOptions.Web);
        _dataSource = dataSourceBuilder.Build();

        var options = CreateOptions();
        await using var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dataSource != null!)
        {
            var options = CreateOptions();
            await using (var dbContext = new AppDbContext(options))
            {
                await dbContext.Database.EnsureDeletedAsync();
            }

            await _dataSource.DisposeAsync();
        }
    }

    private DbContextOptions<AppDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_dataSource)
            .UseSnakeCaseNamingConvention()
            .Options;
    }

    private Segment CreateSegment(string key, string description)
    {
        return new Segment(
            workspaceId: _workspaceId,
            envId: _envId,
            name: key,
            key: key,
            type: SegmentType.EnvironmentSpecific,
            scopes: [],
            included: [],
            excluded: [],
            rules: new List<MatchRule>(),
            description: description
        );
    }

    [Fact]
    public async Task SetPending_LosingRacer_NoOps_And_Winner_Survives()
    {
        const string key = "retry-setpending-race";

        Guid segmentId;

        // Seed the committed row via its own throwaway context/service.
        await using (var seedContext = new AppDbContext(CreateOptions()))
        {
            var seedService = new SegmentService(seedContext, NullLogger<SegmentService>.Instance);
            var committed = CreateSegment(key, "old");
            committed.CommittedVersion = 1;
            await seedService.AddOneAsync(committed);
            segmentId = committed.Id;
        }

        await using var contextA = new AppDbContext(CreateOptions());
        await using var contextB = new AppDbContext(CreateOptions());
        var serviceA = new SegmentService(contextA, NullLogger<SegmentService>.Instance);
        var serviceB = new SegmentService(contextB, NullLogger<SegmentService>.Instance);

        // B "already read v1 state": force contextB to track the row before A's write lands, so
        // its identity map later hands back this same stale instance instead of a fresh query.
        await serviceB.GetAsync(segmentId);

        // A wins the race: fresh read via contextA, stages v2, commits — xmin advances.
        await serviceA.SetPendingAsync(segmentId, CreateSegment(key, "new-a"), version: 2);

        // B, the loser, now attempts to stage an equal version using its stale tracked copy.
        // Internally: guard passes against the STALE state (no pending yet) -> SaveChanges hits
        // the now-advanced xmin -> DbUpdateConcurrencyException -> detach & retry -> fresh read
        // shows A's pending (v2) -> guard (version <= Pending.Version) now fires -> no-op.
        await serviceB.SetPendingAsync(segmentId, CreateSegment(key, "new-b"), version: 2);

        // Assert: no exception escaped, and A's pending is still the one in the row.
        await using var verifyContext = new AppDbContext(CreateOptions());
        var verifyService = new SegmentService(verifyContext, NullLogger<SegmentService>.Instance);
        var raw = await verifyService.GetAsync(segmentId);
        Assert.NotNull(raw.Pending);
        Assert.Equal(2, raw.Pending!.Version);
        Assert.Equal("new-a", raw.Pending.Value.Description);
        Assert.Equal(1, raw.CommittedVersion);
    }

    [Fact]
    public async Task PromotePending_Returns_False_When_Pending_Replaced_Concurrently()
    {
        const string key = "retry-promote-race";

        Guid segmentId;

        await using (var seedContext = new AppDbContext(CreateOptions()))
        {
            var seedService = new SegmentService(seedContext, NullLogger<SegmentService>.Instance);
            var committed = CreateSegment(key, "old");
            committed.CommittedVersion = 1;
            await seedService.AddOneAsync(committed);
            segmentId = committed.Id;
        }

        await using var contextA = new AppDbContext(CreateOptions());
        await using var contextB = new AppDbContext(CreateOptions());
        var serviceA = new SegmentService(contextA, NullLogger<SegmentService>.Instance);
        var serviceB = new SegmentService(contextB, NullLogger<SegmentService>.Instance);

        // Stage v2 via A.
        await serviceA.SetPendingAsync(segmentId, CreateSegment(key, "new-a"), version: 2);

        // B "already read the v2 pending state" (planning to promote expectedVersion 2): force
        // contextB to track the row now, while pending is still v2.
        await serviceB.GetAsync(segmentId);

        // Concurrently, A re-stages a newer pending (v3) — the "replacement".
        await serviceA.SetPendingAsync(segmentId, CreateSegment(key, "new-a-v3"), version: 3);

        // B attempts to promote against its stale expectation (v2). Internally: guard passes
        // against the STALE tracked copy (Pending.Version == 2) -> SaveChanges hits the advanced
        // xmin -> DbUpdateConcurrencyException -> detach & retry -> fresh read shows Pending.Version
        // == 3 -> guard now fires (3 != 2) -> returns false.
        var promoted = await serviceB.PromotePendingAsync(segmentId, expectedVersion: 2);

        Assert.False(promoted);

        // State intact: still v1 committed, v3 pending — B's stale promote attempt did not clobber it.
        await using var verifyContext = new AppDbContext(CreateOptions());
        var verifyService = new SegmentService(verifyContext, NullLogger<SegmentService>.Instance);
        var raw = await verifyService.GetAsync(segmentId);
        Assert.Equal(1, raw.CommittedVersion);
        Assert.NotNull(raw.Pending);
        Assert.Equal(3, raw.Pending!.Version);
        Assert.Equal("new-a-v3", raw.Pending.Value.Description);
    }

    [Fact]
    public async Task Concurrent_Stress_No_Lost_Updates()
    {
        const string key = "retry-stress";

        Guid segmentId;

        await using (var seedContext = new AppDbContext(CreateOptions()))
        {
            var seedService = new SegmentService(seedContext, NullLogger<SegmentService>.Instance);
            var committed = CreateSegment(key, "old");
            committed.CommittedVersion = 1;
            await seedService.AddOneAsync(committed);
            segmentId = committed.Id;
        }

        // 20 parallel tasks (14 stagers + 6 promoters), each with its own AppDbContext +
        // SegmentService, doing mixed
        // SetPendingAsync (strictly increasing versions, so every stage is a legitimate advance)
        // and PromotePendingAsync (a spread of expected versions, some of which will hit and some
        // of which will miss depending on timing).
        var stagedVersions = Enumerable.Range(2, 14).Select(v => (long)v).ToArray(); // 2..15
        var promoteExpectedVersions = new long[] { 4, 6, 8, 10, 12, 14 };

        var options = CreateOptions();

        // A small random jitter before each task's own work spreads the 20 tasks' actual
        // read/write windows out (as real concurrent callers arriving over a short span would
        // be), instead of every task hitting GetAsync at the exact same instant. That thundering-
        // herd extreme (verified separately in #77) can require more retries than the bounded
        // budget (intentionally lets that propagate as a pathological-contention signal) — this
        // test targets the realistic "many overlapping racers" case the retry loop is meant to
        // absorb.
        var setTasks = stagedVersions.Select(version => Task.Run(async () =>
        {
            await Task.Delay(Random.Shared.Next(0, 40));
            await using var context = new AppDbContext(options);
            var service = new SegmentService(context, NullLogger<SegmentService>.Instance);
            await service.SetPendingAsync(segmentId, CreateSegment(key, $"v{version}"), version);
        }));

        var promoteTasks = promoteExpectedVersions.Select(expected => Task.Run(async () =>
        {
            await Task.Delay(Random.Shared.Next(0, 40));
            await using var context = new AppDbContext(options);
            var service = new SegmentService(context, NullLogger<SegmentService>.Instance);
            await service.PromotePendingAsync(segmentId, expected);
        }));

        // Assert: no unhandled exception escapes the retry loops under contention.
        await Task.WhenAll(setTasks.Concat(promoteTasks));

        // Assert: no lost updates — final state is internally consistent and made only of
        // values that were actually written by one of the tasks above.
        await using var verifyContext = new AppDbContext(options);
        var verifyService = new SegmentService(verifyContext, NullLogger<SegmentService>.Instance);
        var raw = await verifyService.GetAsync(segmentId);

        Assert.True(raw.CommittedVersion == 1 || stagedVersions.Contains(raw.CommittedVersion));
        if (raw.Pending != null)
        {
            Assert.True(raw.Pending.Version > raw.CommittedVersion);
            Assert.Contains(raw.Pending.Version, stagedVersions);
        }
    }
}
