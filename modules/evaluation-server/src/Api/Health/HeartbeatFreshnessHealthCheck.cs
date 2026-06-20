using System.Diagnostics.Metrics;
using Api.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Api.Health;

/// <summary>
/// D5 (#22) self-fence / readiness signal. Reports whether this evaluation-server pod is still
/// able to publish heartbeats to the control plane.
/// </summary>
/// <remarks>
/// <para>
/// Behavior is mode-dependent:
/// <list type="bullet">
/// <item><b>BestEffort</b> — no-op: always <see cref="HealthStatus.Healthy"/>. Heartbeat freshness
/// is irrelevant when changes are not cross-DC gated.</item>
/// <item><b>GatedCommit</b> — when the pod has been unable to publish a heartbeat for longer than
/// <c>ControlPlane:HeartbeatStalenessThresholdSeconds</c> (it has likely been evicted / partitioned
/// from the control plane), report <see cref="HealthStatus.Degraded"/> — NOT
/// <see cref="HealthStatus.Unhealthy"/>. The pod keeps serving its consistent last-committed values;
/// we surface degradation for operators rather than pulling it from rotation.</item>
/// </list>
/// </para>
/// <para>
/// <b>Readiness:</b> the ASP.NET Core health middleware maps both <c>Healthy</c> and <c>Degraded</c>
/// to HTTP 200 by default (only <c>Unhealthy</c> → 503), and the eval-server readiness endpoint does
/// not override that mapping. Therefore a <c>Degraded</c> result keeps <c>/health/readiness</c>
/// passing — this check is observational, NOT a hard readiness fence.
/// </para>
/// <para>
/// Also emits the <c>evaluation_server.consistency.heartbeat_staleness_seconds</c> observable gauge
/// (seconds since last successful publish; 0 when healthy) and logs a warning on the transition into
/// degraded.
/// </para>
/// </remarks>
public sealed class HeartbeatFreshnessHealthCheck : IHealthCheck
{
    /// <summary>
    /// Stable meter name for evaluation-server consistency observability. Mirrors the control
    /// plane's <c>FeatBit.ControlPlane.Consistency</c> meter naming.
    /// </summary>
    public const string MeterName = "FeatBit.EvaluationServer.Consistency";

    /// <summary>
    /// Seconds since this pod last successfully published a heartbeat (0 when fresh / healthy /
    /// not applicable). Operators alert on a sustained non-zero value under GatedCommit.
    /// </summary>
    public const string StalenessGaugeName = "evaluation_server.consistency.heartbeat_staleness_seconds";

    /// <summary>
    /// Default staleness threshold (seconds) when <c>ControlPlane:HeartbeatStalenessThresholdSeconds</c>
    /// is unset / non-positive. 180s = 3× the default 60s heartbeat interval, i.e. several missed
    /// heartbeats — long enough to avoid flapping on a single transient publish failure.
    /// </summary>
    public const int DefaultStalenessThresholdSeconds = 180;

    private static readonly Meter Meter = new(MeterName);

    // Most recent staleness (seconds), refreshed on every health-check evaluation. Static + volatile
    // so a single gauge registration survives across check instances and the gauge callback may run
    // on a different thread than the check evaluation.
    private static volatile int _stalenessSeconds;

    private static readonly ObservableGauge<long> StalenessGauge = Meter.CreateObservableGauge(
        StalenessGaugeName,
        () => (long)_stalenessSeconds,
        unit: "s",
        description:
        "Seconds since this evaluation-server pod last successfully published a heartbeat to the " +
        "control plane; 0 when healthy / not applicable (BestEffort).");

    private readonly IHeartbeatPublishStatus _status;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HeartbeatFreshnessHealthCheck> _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly DateTimeOffset _startedAt;

    // Tracks the previous degraded state so the warning is logged only on the transition into
    // degraded (not on every poll while degraded persists).
    private int _wasDegraded;

    public HeartbeatFreshnessHealthCheck(
        IHeartbeatPublishStatus status,
        IConfiguration configuration,
        ILogger<HeartbeatFreshnessHealthCheck> logger)
        : this(status, configuration, logger, now: null)
    {
    }

    /// <summary>
    /// Testable constructor: <paramref name="now"/> injects the clock so freshness can be exercised
    /// without timers. When <paramref name="now"/> is supplied, the startup-grace baseline is the
    /// first value it returns.
    /// </summary>
    internal HeartbeatFreshnessHealthCheck(
        IHeartbeatPublishStatus status,
        IConfiguration configuration,
        ILogger<HeartbeatFreshnessHealthCheck> logger,
        Func<DateTimeOffset>? now)
    {
        _status = status;
        _configuration = configuration;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _startedAt = _now();
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // BestEffort: heartbeat freshness is irrelevant — always healthy, gauge stays at 0.
        if (_configuration.GetConsistencyMode() != ConsistencyMode.GatedCommit)
        {
            _stalenessSeconds = 0;
            return Task.FromResult(HealthCheckResult.Healthy(
                "Heartbeat freshness not gating (BestEffort)."));
        }

        var thresholdSeconds = GetThresholdSeconds();
        var threshold = TimeSpan.FromSeconds(thresholdSeconds);
        var now = _now();
        var dcId = _configuration.GetDcId() ?? "(unset)";

        var lastSuccess = _status.LastSuccessfulPublishAt;

        TimeSpan age;
        if (lastSuccess.HasValue)
        {
            age = now - lastSuccess.Value;
        }
        else
        {
            // Never published since startup: measure from process start so a just-started pod gets
            // a startup grace equal to the threshold before it can be considered stale.
            age = now - _startedAt;
        }

        var ageSeconds = (int)Math.Max(0, age.TotalSeconds);
        _stalenessSeconds = ageSeconds;

        var isStale = age > threshold;

        if (!isStale)
        {
            Interlocked.Exchange(ref _wasDegraded, 0);

            var healthyMessage = lastSuccess.HasValue
                ? $"Heartbeat fresh: DcId={dcId}, {ageSeconds}s since last successful publish " +
                  $"(threshold {thresholdSeconds}s)."
                : $"Within startup grace: DcId={dcId}, {ageSeconds}s since start " +
                  $"(threshold {thresholdSeconds}s).";

            return Task.FromResult(HealthCheckResult.Healthy(healthyMessage));
        }

        var message = lastSuccess.HasValue
            ? $"Heartbeat stale: DcId={dcId} has not published a heartbeat for {ageSeconds}s " +
              $"(threshold {thresholdSeconds}s); pod likely evicted/partitioned from the control " +
              "plane. Serving last-committed values."
            : $"No heartbeat published since startup: DcId={dcId}, {ageSeconds}s since start " +
              $"(threshold {thresholdSeconds}s); pod cannot reach the control plane. Serving " +
              "last-committed values.";

        // Log a warning only on the transition into degraded to avoid log spam.
        if (Interlocked.Exchange(ref _wasDegraded, 1) == 0)
        {
            _logger.LogWarning(
                "Heartbeat freshness degraded: DcId={DcId}, StalenessSeconds={StalenessSeconds}, " +
                "ThresholdSeconds={ThresholdSeconds}. Pod likely evicted/partitioned from the " +
                "control plane; continuing to serve last-committed values.",
                dcId, ageSeconds, thresholdSeconds);
        }

        return Task.FromResult(HealthCheckResult.Degraded(message));
    }

    private int GetThresholdSeconds()
    {
        var configured = _configuration.GetValue<int>("ControlPlane:HeartbeatStalenessThresholdSeconds");
        return configured > 0 ? configured : DefaultStalenessThresholdSeconds;
    }
}
