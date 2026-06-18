using Application.Configuration;
using Infrastructure.AppService;
using Infrastructure.Caches.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Application.IntegrationTests.AppService;

/// <summary>
/// Integration tests for the B5 staged-flag version GC worker (<see cref="StagedFlagGcWorker"/>).
///
/// These tests require a real Redis instance. The orchestrating issue (#12) spins one up on a
/// NON-default port (6382) because 6379/6380/6381 are taken by other projects:
///   docker run -d --rm -p 6382:6379 --name b5-redis redis:7-alpine
///
/// The connection string can be overridden via the B5_REDIS env var. xunit 2.x has no runtime
/// Assert.Skip, so if Redis is unreachable the tests fail loudly with a clear message rather
/// than passing silently — the orchestrating issue requires verification against a real Redis.
/// </summary>
public class StagedFlagGcWorkerTests : IAsyncLifetime
{
    private const string DefaultConnection = "localhost:6382";

    private ConnectionMultiplexer? _mux;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("B5_REDIS") ?? DefaultConnection;

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
                "docker run -d --rm -p 6382:6379 --name b5-redis redis:7-alpine " +
                "(or set the B5_REDIS env var).");
        }
    }

    public Task DisposeAsync()
    {
        _mux?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunGcOnce_DeletesSupersededVersions_KeepsCommittedAndPointer()
    {
        var db = _mux!.GetDatabase();
        var sut = CreateSut();

        var flagId = Guid.NewGuid();
        const long v1 = 1000L;
        const long v2 = 2000L;

        var v1Key = RedisCaches.FlagVersion(flagId, v1);
        var v2Key = RedisCaches.FlagVersion(flagId, v2);
        var pointerKey = RedisCaches.FlagCommittedPointer(flagId);

        // seed: two versioned value keys + a committed pointer at v2.
        await db.StringSetAsync(v1Key, "v1-value");
        await db.StringSetAsync(v2Key, "v2-value");
        await db.StringSetAsync(pointerKey, v2);

        try
        {
            var deleted = await sut.RunGcOnceAsync();

            Assert.True(deleted >= 1, "at least the superseded :v1 key should be deleted");

            // ACCEPTANCE: superseded version is gone.
            Assert.False(await db.KeyExistsAsync(v1Key), "superseded :v1000 key must be deleted");

            // ACCEPTANCE: committed version + pointer are intact.
            Assert.True(await db.KeyExistsAsync(v2Key), "committed :v2000 key must be kept");
            Assert.Equal(v2.ToString(), (string?)await db.StringGetAsync(pointerKey));
        }
        finally
        {
            await db.KeyDeleteAsync(v1Key);
            await db.KeyDeleteAsync(v2Key);
            await db.KeyDeleteAsync(pointerKey);
        }
    }

    [Fact]
    public async Task RunGcOnce_LeavesFlagWithoutCommittedPointerUntouched()
    {
        var db = _mux!.GetDatabase();
        var sut = CreateSut();

        var flagId = Guid.NewGuid();
        const long v1 = 1000L;
        const long v2 = 2000L;

        var v1Key = RedisCaches.FlagVersion(flagId, v1);
        var v2Key = RedisCaches.FlagVersion(flagId, v2);
        var pointerKey = RedisCaches.FlagCommittedPointer(flagId);

        // seed: versioned value keys but NO committed pointer.
        await db.StringSetAsync(v1Key, "v1-value");
        await db.StringSetAsync(v2Key, "v2-value");

        try
        {
            await sut.RunGcOnceAsync();

            // ACCEPTANCE: no committed pointer => nothing is deleted.
            Assert.True(await db.KeyExistsAsync(v1Key), ":v1000 must be kept (no committed pointer)");
            Assert.True(await db.KeyExistsAsync(v2Key), ":v2000 must be kept (no committed pointer)");
            Assert.False(await db.KeyExistsAsync(pointerKey));
        }
        finally
        {
            await db.KeyDeleteAsync(v1Key);
            await db.KeyDeleteAsync(v2Key);
        }
    }

    private StagedFlagGcWorker CreateSut()
    {
        // GatedCommit so the worker is enabled; RunGcOnceAsync runs the sweep regardless of timer.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:ConsistencyMode"] = nameof(ConsistencyMode.GatedCommit)
            })
            .Build();

        return new StagedFlagGcWorker(
            new TestRedisClient(_mux!),
            configuration,
            NullLogger<StagedFlagGcWorker>.Instance);
    }

    private sealed class TestRedisClient(IConnectionMultiplexer connection) : IRedisClient
    {
        public IConnectionMultiplexer Connection { get; } = connection;

        public IDatabase GetDatabase() => Connection.GetDatabase();
    }
}
