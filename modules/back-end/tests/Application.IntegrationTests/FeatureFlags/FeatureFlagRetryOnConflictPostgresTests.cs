using Domain.FeatureFlags;
using Domain.Utils;
using Infrastructure.Persistence.EntityFrameworkCore;
using Infrastructure.Services.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Application.IntegrationTests.FeatureFlags;

/// <summary>
/// #72b acceptance: with the xmin concurrency token in place (#76), FeatureFlagService's
/// SetPendingAsync/PromotePendingAsync survive a racing writer's DbUpdateConcurrencyException by
/// detaching the stale tracked entity and retrying — the version guards re-evaluate against the
/// fresh row, converging to the same semantics the Mongo provider gets from its version-filtered
/// UpdateOneAsync/ReplaceOneAsync: the loser of a race either no-ops (SetPendingAsync) or returns
/// false (PromotePendingAsync); it never throws and never clobbers the winner.
///
/// Integration test against a real Postgres instance (throwaway container on port 5434).
/// </summary>
[Trait("Category", "Integration")]
public sealed class FeatureFlagRetryOnConflictPostgresTests : IAsyncLifetime
{
    // The throwaway Postgres container is started on 5434 with database "featbit".
    private const string BaseConnectionString =
        "Host=localhost;Port=5434;Database=featbit;Username=postgres;Password=please_change_me";

    // Use a UNIQUE throwaway database per test instance so EnsureCreated/EnsureDeleted in
    // different tests do not race each other. EF creates and drops this database.
    private readonly string _dbName = $"featbit_72b_test_{Guid.NewGuid():N}";
    private NpgsqlDataSource _dataSource = null!;
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
        // columns (Variations/Rules/Fallthrough/Pending), and the snake_case naming convention
        // is required so EnsureCreated materializes the expected columns (incl. the xmin token).
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

    private FeatureFlag CreateFlag(string key, bool isEnabled = false)
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

    [Fact]
    public async Task SetPending_LosingRacer_NoOps_And_Winner_Survives()
    {
        const string key = "retry-setpending-race";

        // Seed the committed row via its own throwaway context/service.
        await using (var seedContext = new AppDbContext(CreateOptions()))
        {
            var seedService = new FeatureFlagService(seedContext);
            var committed = CreateFlag(key, isEnabled: false);
            committed.CommittedVersion = 1;
            await seedService.AddOneAsync(committed);
        }

        await using var contextA = new AppDbContext(CreateOptions());
        await using var contextB = new AppDbContext(CreateOptions());
        var serviceA = new FeatureFlagService(contextA);
        var serviceB = new FeatureFlagService(contextB);

        // B "already read v1 state": force contextB to track the row before A's write lands, so
        // its identity map later hands back this same stale instance instead of a fresh query.
        await serviceB.GetAsync(_envId, key);

        // A wins the race: fresh read via contextA, stages v2, commits — xmin advances.
        await serviceA.SetPendingAsync(_envId, key, CreateFlag(key, isEnabled: true), version: 2);

        // B, the loser, now attempts to stage an equal version using its stale tracked copy.
        // Internally: guard passes against the STALE state (no pending yet) -> SaveChanges hits
        // the now-advanced xmin -> DbUpdateConcurrencyException -> detach & retry -> fresh read
        // shows A's pending (v2) -> guard (version <= Pending.Version) now fires -> no-op.
        await serviceB.SetPendingAsync(_envId, key, CreateFlag(key, isEnabled: false), version: 2);

        // Assert: no exception escaped, and A's pending is still the one in the row.
        await using var verifyContext = new AppDbContext(CreateOptions());
        var verifyService = new FeatureFlagService(verifyContext);
        var raw = await verifyService.GetAsync(_envId, key);
        Assert.NotNull(raw.Pending);
        Assert.Equal(2, raw.Pending!.Version);
        Assert.True(raw.Pending.Value.IsEnabled);
        Assert.Equal(1, raw.CommittedVersion);
    }

    [Fact]
    public async Task PromotePending_Returns_False_When_Pending_Replaced_Concurrently()
    {
        const string key = "retry-promote-race";

        await using (var seedContext = new AppDbContext(CreateOptions()))
        {
            var seedService = new FeatureFlagService(seedContext);
            var committed = CreateFlag(key, isEnabled: false);
            committed.CommittedVersion = 1;
            await seedService.AddOneAsync(committed);
        }

        await using var contextA = new AppDbContext(CreateOptions());
        await using var contextB = new AppDbContext(CreateOptions());
        var serviceA = new FeatureFlagService(contextA);
        var serviceB = new FeatureFlagService(contextB);

        // Stage v2 via A.
        await serviceA.SetPendingAsync(_envId, key, CreateFlag(key, isEnabled: true), version: 2);

        // B "already read the v2 pending state" (planning to promote expectedVersion 2): force
        // contextB to track the row now, while pending is still v2.
        await serviceB.GetAsync(_envId, key);

        // Concurrently, A re-stages a newer pending (v3) — the "replacement".
        await serviceA.SetPendingAsync(_envId, key, CreateFlag(key, isEnabled: false), version: 3);

        // B attempts to promote against its stale expectation (v2). Internally: guard passes
        // against the STALE tracked copy (Pending.Version == 2) -> SaveChanges hits the advanced
        // xmin -> DbUpdateConcurrencyException -> detach & retry -> fresh read shows Pending.Version
        // == 3 -> guard now fires (3 != 2) -> returns false.
        var promoted = await serviceB.PromotePendingAsync(_envId, key, expectedVersion: 2);

        Assert.False(promoted);

        // State intact: still v1 committed, v3 pending — B's stale promote attempt did not clobber it.
        await using var verifyContext = new AppDbContext(CreateOptions());
        var verifyService = new FeatureFlagService(verifyContext);
        var raw = await verifyService.GetAsync(_envId, key);
        Assert.Equal(1, raw.CommittedVersion);
        Assert.NotNull(raw.Pending);
        Assert.Equal(3, raw.Pending!.Version);
        Assert.False(raw.Pending.Value.IsEnabled);
    }

    [Fact]
    public async Task Concurrent_Stress_No_Lost_Updates()
    {
        const string key = "retry-stress";

        await using (var seedContext = new AppDbContext(CreateOptions()))
        {
            var seedService = new FeatureFlagService(seedContext);
            var committed = CreateFlag(key, isEnabled: false);
            committed.CommittedVersion = 1;
            await seedService.AddOneAsync(committed);
        }

        // 20 parallel tasks (14 stagers + 6 promoters), each with its own AppDbContext +
        // FeatureFlagService, doing mixed
        // SetPendingAsync (strictly increasing versions, so every stage is a legitimate advance)
        // and PromotePendingAsync (a spread of expected versions, some of which will hit and some
        // of which will miss depending on timing).
        var stagedVersions = Enumerable.Range(2, 14).Select(v => (long)v).ToArray(); // 2..15
        var promoteExpectedVersions = new long[] { 4, 6, 8, 10, 12, 14 };

        var options = CreateOptions();

        // A small random jitter before each task's own work spreads the 20 tasks' actual
        // read/write windows out (as real concurrent callers arriving over a short span would
        // be), instead of every task hitting GetAsync at the exact same instant. That thundering-
        // herd extreme (verified separately) can require more retries than the bounded budget
        // (#77 intentionally lets that propagate as a pathological-contention signal) — this test
        // targets the realistic "many overlapping racers" case the retry loop is meant to absorb.
        var setTasks = stagedVersions.Select(version => Task.Run(async () =>
        {
            await Task.Delay(Random.Shared.Next(0, 40));
            await using var context = new AppDbContext(options);
            var service = new FeatureFlagService(context);
            await service.SetPendingAsync(_envId, key, CreateFlag(key, isEnabled: version % 2 == 0), version);
        }));

        var promoteTasks = promoteExpectedVersions.Select(expected => Task.Run(async () =>
        {
            await Task.Delay(Random.Shared.Next(0, 40));
            await using var context = new AppDbContext(options);
            var service = new FeatureFlagService(context);
            await service.PromotePendingAsync(_envId, key, expected);
        }));

        // Assert: no unhandled exception escapes the retry loops under contention.
        await Task.WhenAll(setTasks.Concat(promoteTasks));

        // Assert: no lost updates — final state is internally consistent and made only of
        // values that were actually written by one of the tasks above.
        await using var verifyContext = new AppDbContext(options);
        var verifyService = new FeatureFlagService(verifyContext);
        var raw = await verifyService.GetAsync(_envId, key);

        Assert.True(raw.CommittedVersion == 1 || stagedVersions.Contains(raw.CommittedVersion));
        if (raw.Pending != null)
        {
            Assert.True(raw.Pending.Version > raw.CommittedVersion);
            Assert.Contains(raw.Pending.Version, stagedVersions);
        }
    }
}
