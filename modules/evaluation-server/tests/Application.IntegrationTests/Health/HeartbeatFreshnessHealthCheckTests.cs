using Api.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Application.IntegrationTests.Health;

/// <summary>
/// Pure unit tests for <see cref="HeartbeatFreshnessHealthCheck"/> (no infra). The clock and the
/// publish-status singleton are injected so freshness can be exercised deterministically without
/// timers.
/// </summary>
public class HeartbeatFreshnessHealthCheckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock(DateTimeOffset start)
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset Now() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static HeartbeatFreshnessHealthCheck CreateSut(
        IConfiguration configuration,
        IHeartbeatPublishStatus status,
        FakeClock clock)
        => new(
            status,
            configuration,
            NullLogger<HeartbeatFreshnessHealthCheck>.Instance,
            clock.Now);

    private static Task<HealthCheckResult> Check(HeartbeatFreshnessHealthCheck sut)
        => sut.CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task BestEffort_IsAlwaysHealthy_RegardlessOfHeartbeatAge()
    {
        var config = Config(
            ("ControlPlane:ConsistencyMode", "BestEffort"),
            ("ControlPlane:HeartbeatStalenessThresholdSeconds", "10"));
        var status = new HeartbeatPublishStatus();
        status.MarkSuccess(T0); // very stale relative to the clock below
        var clock = new FakeClock(T0.AddHours(1));

        var result = await Check(CreateSut(config, status, clock));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task GatedCommit_Fresh_IsHealthy()
    {
        var config = Config(
            ("ControlPlane:ConsistencyMode", "GatedCommit"),
            ("ControlPlane:HeartbeatStalenessThresholdSeconds", "60"));
        var clock = new FakeClock(T0);
        var status = new HeartbeatPublishStatus();
        status.MarkSuccess(T0);

        clock.Advance(TimeSpan.FromSeconds(10)); // within threshold

        var result = await Check(CreateSut(config, status, clock));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task GatedCommit_Stale_IsDegraded_AndNamesDcIdAndAge()
    {
        var config = Config(
            ("ControlPlane:ConsistencyMode", "GatedCommit"),
            ("ControlPlane:DcId", "dc-west"),
            ("ControlPlane:HeartbeatStalenessThresholdSeconds", "60"));
        var clock = new FakeClock(T0);
        var status = new HeartbeatPublishStatus();
        status.MarkSuccess(T0);

        clock.Advance(TimeSpan.FromSeconds(120)); // past the 60s threshold

        var result = await Check(CreateSut(config, status, clock));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("dc-west", result.Description);
        Assert.Contains("120", result.Description);
    }

    [Fact]
    public async Task GatedCommit_NeverPublished_WithinStartupGrace_IsHealthy()
    {
        var config = Config(
            ("ControlPlane:ConsistencyMode", "GatedCommit"),
            ("ControlPlane:HeartbeatStalenessThresholdSeconds", "60"));
        var clock = new FakeClock(T0); // startedAt captured at T0
        var status = new HeartbeatPublishStatus(); // never published

        var sut = CreateSut(config, status, clock);
        clock.Advance(TimeSpan.FromSeconds(30)); // still within startup grace (< 60s)

        var result = await Check(sut);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task GatedCommit_NeverPublished_PastStartupGrace_IsDegraded()
    {
        var config = Config(
            ("ControlPlane:ConsistencyMode", "GatedCommit"),
            ("ControlPlane:DcId", "dc-east"),
            ("ControlPlane:HeartbeatStalenessThresholdSeconds", "60"));
        var clock = new FakeClock(T0);
        var status = new HeartbeatPublishStatus(); // never published

        var sut = CreateSut(config, status, clock);
        clock.Advance(TimeSpan.FromSeconds(120)); // past startup grace

        var result = await Check(sut);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("dc-east", result.Description);
    }

    [Fact]
    public async Task GatedCommit_UsesDefaultThreshold_WhenUnset()
    {
        var config = Config(
            ("ControlPlane:ConsistencyMode", "GatedCommit"));
        var clock = new FakeClock(T0);
        var status = new HeartbeatPublishStatus();
        status.MarkSuccess(T0);

        // Just under the documented default (180s) -> still healthy.
        clock.Advance(TimeSpan.FromSeconds(HeartbeatFreshnessHealthCheck.DefaultStalenessThresholdSeconds - 1));
        Assert.Equal(HealthStatus.Healthy, (await Check(CreateSut(config, status, clock))).Status);

        // Past the default -> degraded.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HealthStatus.Degraded, (await Check(CreateSut(config, status, clock))).Status);
    }

    [Fact]
    public void MarkFailure_DoesNotAdvanceLastSuccess()
    {
        var status = new HeartbeatPublishStatus();
        status.MarkSuccess(T0);

        status.MarkFailure(T0.AddSeconds(30));

        Assert.Equal(T0, status.LastSuccessfulPublishAt);
    }

    [Fact]
    public void LastSuccessfulPublishAt_IsNull_BeforeFirstSuccess()
    {
        var status = new HeartbeatPublishStatus();

        Assert.Null(status.LastSuccessfulPublishAt);
    }

    /// <summary>
    /// Readiness contract: ASP.NET Core's default health-result mapping treats
    /// <see cref="HealthStatus.Degraded"/> as HTTP 200 (ready); only
    /// <see cref="HealthStatus.Unhealthy"/> returns 503. The eval-server readiness endpoint does not
    /// override that mapping (see <c>MiddlewaresRegister</c>), so a degraded heartbeat keeps
    /// <c>/health/readiness</c> passing. This check must therefore NEVER return Unhealthy — it only
    /// ever returns Healthy or Degraded — so it can never fail readiness.
    /// </summary>
    [Fact]
    public async Task NeverReturnsUnhealthy_AcrossModesAndAges()
    {
        // BestEffort with a very stale heartbeat.
        var bestEffort = Config(("ControlPlane:ConsistencyMode", "BestEffort"));
        var s1 = new HeartbeatPublishStatus();
        s1.MarkSuccess(T0);
        Assert.NotEqual(
            HealthStatus.Unhealthy,
            (await Check(CreateSut(bestEffort, s1, new FakeClock(T0.AddHours(1))))).Status);

        // GatedCommit, far past the threshold and never published — the worst case.
        var gated = Config(
            ("ControlPlane:ConsistencyMode", "GatedCommit"),
            ("ControlPlane:HeartbeatStalenessThresholdSeconds", "30"));
        var clock = new FakeClock(T0);
        var sut = CreateSut(gated, new HeartbeatPublishStatus(), clock);
        clock.Advance(TimeSpan.FromHours(1));
        var result = await Check(sut);
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);
    }
}
