using Api.Application.ControlPlane;
using Api.Infrastructure.Caches;
using Application.Configuration;
using Infrastructure.Caches.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Api.UnitTests.Application.ControlPlane;

public class CacheReconcilerTests
{
    private static readonly CommittedSnapshot EmptySnapshot = new(
        Array.Empty<Domain.FeatureFlags.FeatureFlag>(),
        Array.Empty<Domain.Segments.Segment>(),
        new Dictionary<string, ICollection<Guid>>(),
        Array.Empty<Domain.Environments.SecretCache>());

    private readonly Mock<IDcBackfiller> _backfiller = new();

    public CacheReconcilerTests()
    {
        // Default: backfill succeeds, returning 0 flags. Individual tests override as needed.
        _backfiller
            .Setup(b => b.BackfillDcAsync(It.IsAny<string>(), It.IsAny<ConsistencyMode>(), It.IsAny<CommittedSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        // #90: RunOnceAsync fetches the shared snapshot lazily (only when a DC is newly reachable);
        // tests that DO expect a backfill rely on this succeeding.
        _backfiller
            .Setup(b => b.FetchCommittedSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptySnapshot);
    }

    // A DC whose IsConnected is evaluated live each poll via the supplied delegate.
    private static DcRedisConnection Dc(string dcId, Func<bool> isConnected, bool isLocal = false)
    {
        var mux = new Mock<IConnectionMultiplexer>();
        mux.SetupGet(m => m.IsConnected).Returns(isConnected);
        var client = new Mock<IRedisClient>();
        client.SetupGet(c => c.Connection).Returns(mux.Object);
        return new DcRedisConnection(dcId, client.Object, isLocal);
    }

    // A DC whose connection state cannot be read (simulates a transient probe failure).
    private static DcRedisConnection ThrowingDc(string dcId)
    {
        var client = new Mock<IRedisClient>();
        client.SetupGet(c => c.Connection).Throws(new Exception("cannot read connection state"));
        return new DcRedisConnection(dcId, client.Object, false);
    }

    private CacheReconciler CreateSut(
        IReadOnlyList<DcRedisConnection> dcs,
        string consistencyMode = "BestEffort",
        bool enabled = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:ConsistencyMode"] = consistencyMode,
                ["ControlPlane:CacheReconcile:Enabled"] = enabled.ToString(),
                ["ControlPlane:CacheReconcile:IntervalSeconds"] = "1"
            })
            .Build();

        return new CacheReconciler(
            dcs,
            _backfiller.Object,
            configuration,
            NullLogger<CacheReconciler>.Instance);
    }

    private void VerifyBackfill(string dcId, ConsistencyMode mode, Times times) =>
        _backfiller.Verify(
            b => b.BackfillDcAsync(dcId, mode, It.IsAny<CommittedSnapshot>(), It.IsAny<CancellationToken>()),
            times);

    private void VerifySnapshotFetch(Times times) =>
        _backfiller.Verify(b => b.FetchCommittedSnapshotAsync(It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task FirstSeenConnectedPeer_IsBackfilledOnce()
    {
        var sut = CreateSut(new[] { Dc("east", () => true) });

        await sut.RunOnceAsync();

        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Once());
    }

    [Fact]
    public async Task LocalDc_IsBackfilledOnStartup()
    {
        // The local DC's in-cluster link is reachable on startup -> first-seen -> backfill from the
        // source of truth. This is the "local refill" that heals a recovered cluster even with no peer.
        var sut = CreateSut(new[] { Dc("west", () => true, isLocal: true) });

        await sut.RunOnceAsync();

        VerifyBackfill("west", ConsistencyMode.BestEffort, Times.Once());
    }

    [Fact]
    public async Task LocalAndPeer_BothBackfilledOnStartup()
    {
        var sut = CreateSut(new[]
        {
            Dc("west", () => true, isLocal: true),
            Dc("east", () => true)
        });

        await sut.RunOnceAsync();

        VerifyBackfill("west", ConsistencyMode.BestEffort, Times.Once());
        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Once());
    }

    [Fact]
    public async Task SteadyConnected_IsNotReBackfilledOnLaterTicks()
    {
        var sut = CreateSut(new[] { Dc("east", () => true) });

        await sut.RunOnceAsync();
        await sut.RunOnceAsync();
        await sut.RunOnceAsync();

        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Once());
    }

    [Fact]
    public async Task DisconnectedThenReconnected_TriggersExactlyOneBackfill()
    {
        var connected = false;
        var sut = CreateSut(new[] { Dc("east", () => connected) });

        await sut.RunOnceAsync(); // down -> no backfill
        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Never());

        connected = true;
        await sut.RunOnceAsync(); // false -> true transition -> backfill
        await sut.RunOnceAsync(); // steady connected -> no further backfill

        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Once());
    }

    [Theory]
    [InlineData("BestEffort")]
    [InlineData("GatedCommit")]
    public async Task PassesConfiguredConsistencyMode_ToBackfiller(string mode)
    {
        var sut = CreateSut(new[] { Dc("east", () => true) }, consistencyMode: mode);

        await sut.RunOnceAsync();

        VerifyBackfill("east", Enum.Parse<ConsistencyMode>(mode), Times.Once());
    }

    [Fact]
    public async Task EmptyDcs_NeverBackfills()
    {
        var sut = CreateSut(Array.Empty<DcRedisConnection>());

        await sut.RunOnceAsync();

        _backfiller.Verify(
            b => b.BackfillDcAsync(It.IsAny<string>(), It.IsAny<ConsistencyMode>(), It.IsAny<CommittedSnapshot>(), It.IsAny<CancellationToken>()),
            Times.Never());
        VerifySnapshotFetch(Times.Never());
    }

    [Fact]
    public async Task Disabled_DoesNotBackfill_WhenHostedServiceRuns()
    {
        var sut = CreateSut(new[] { Dc("east", () => true) }, enabled: false);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        _backfiller.Verify(
            b => b.BackfillDcAsync(It.IsAny<string>(), It.IsAny<ConsistencyMode>(), It.IsAny<CommittedSnapshot>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    // ----- #90: lazy snapshot fetch -----

    [Fact]
    public async Task IdleTick_WithNothingNewlyReachable_NeverFetchesSnapshot()
    {
        // A DC already steady-connected from a previous tick: this tick finds nothing newly
        // reachable, so the shared committed snapshot must NOT be fetched (no DB read on an idle
        // ~10s tick).
        var sut = CreateSut(new[] { Dc("east", () => true) });

        await sut.RunOnceAsync(); // first-seen -> backfills, fetches snapshot once
        VerifySnapshotFetch(Times.Once());

        await sut.RunOnceAsync(); // steady-state idle tick -> no new fetch
        await sut.RunOnceAsync();

        VerifySnapshotFetch(Times.Once());
        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Once());
    }

    [Fact]
    public async Task NoDcsConfigured_NeverFetchesSnapshot()
    {
        var sut = CreateSut(Array.Empty<DcRedisConnection>());

        await sut.RunOnceAsync();

        VerifySnapshotFetch(Times.Never());
    }

    [Fact]
    public async Task MultipleNewlyReachableDcsInOneTick_ShareOneSnapshotFetch()
    {
        // Both DCs first-seen in the SAME tick -> both backfilled, but the snapshot is fetched once
        // and shared, not once per DC.
        var sut = CreateSut(new[]
        {
            Dc("west", () => true, isLocal: true),
            Dc("east", () => true)
        });

        await sut.RunOnceAsync();

        VerifySnapshotFetch(Times.Once());
        VerifyBackfill("west", ConsistencyMode.BestEffort, Times.Once());
        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Once());
    }

    [Fact]
    public async Task UnreadableConnectionState_IsSwallowed_AndOtherDcsStillReconcile()
    {
        var sut = CreateSut(new[] { ThrowingDc("bad"), Dc("east", () => true) });

        await sut.RunOnceAsync(); // must not throw

        VerifyBackfill("bad", ConsistencyMode.BestEffort, Times.Never());
        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Once());
    }

    [Fact]
    public async Task ConcurrentReconcileForSameDc_IsCoalesced()
    {
        var gate = new TaskCompletionSource();
        _backfiller
            .Setup(b => b.BackfillDcAsync("east", It.IsAny<ConsistencyMode>(), It.IsAny<CommittedSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, ConsistencyMode _, CommittedSnapshot _, CancellationToken _) =>
            {
                await gate.Task;
                return 0;
            });

        var sut = CreateSut(new[] { Dc("east", () => true) });

        var first = sut.RunReconcileAsync("east", isLocal: false, EmptySnapshot);  // acquires slot, blocks on gate
        var second = sut.RunReconcileAsync("east", isLocal: false, EmptySnapshot); // sees in-flight -> returns now

        await second;
        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Once());

        gate.SetResult();
        await first;

        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Once());
    }

    [Fact]
    public async Task BackfillFailure_IsRetriedOnNextTick()
    {
        var calls = 0;
        _backfiller
            .Setup(b => b.BackfillDcAsync("east", It.IsAny<ConsistencyMode>(), It.IsAny<CommittedSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ConsistencyMode _, CommittedSnapshot _, CancellationToken _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<int>(new Exception("backfill boom"))
                    : Task.FromResult(0);
            });

        var sut = CreateSut(new[] { Dc("east", () => true) });

        await sut.RunOnceAsync(); // first attempt fails -> watermark cleared
        await sut.RunOnceAsync(); // re-detected as newly reachable -> retried

        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Exactly(2));
    }

    [Fact]
    public async Task SnapshotFetchFailure_IsRetriedOnNextTick_ForAllNewlyReachableDcs()
    {
        var calls = 0;
        _backfiller
            .Setup(b => b.FetchCommittedSnapshotAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<CommittedSnapshot>(new Exception("snapshot fetch boom"))
                    : Task.FromResult(EmptySnapshot);
            });

        var sut = CreateSut(new[]
        {
            Dc("west", () => true, isLocal: true),
            Dc("east", () => true)
        });

        await sut.RunOnceAsync(); // snapshot fetch fails -> neither DC backfilled, watermark cleared
        VerifyBackfill("west", ConsistencyMode.BestEffort, Times.Never());
        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Never());

        await sut.RunOnceAsync(); // both re-detected as newly reachable -> fetch succeeds -> both backfilled

        VerifySnapshotFetch(Times.Exactly(2));
        VerifyBackfill("west", ConsistencyMode.BestEffort, Times.Once());
        VerifyBackfill("east", ConsistencyMode.BestEffort, Times.Once());
    }
}
