using System.Diagnostics.Metrics;
using Api.Application.ControlPlane;
using Microsoft.Extensions.Logging;
using Moq;

namespace Api.UnitTests.Application.ControlPlane;

/// <summary>
/// #71 leader election is opt-in (default off): <see cref="AlwaysLeaderElection"/> is the
/// <see cref="ILeaderElection"/> served while it is disabled. Verifies it always reports
/// leadership, logs a discoverability hint on startup, and still emits the shared
/// <see cref="RedisLeaderElector.IsLeaderGaugeName"/> gauge (same name/tag as
/// <see cref="RedisLeaderElector"/>) pinned at 1.
/// </summary>
public sealed class AlwaysLeaderElectionTests
{
    private readonly Mock<ILogger<AlwaysLeaderElection>> _logger = new();

    [Fact]
    public void IsLeader_IsAlwaysTrue()
    {
        using var sut = new AlwaysLeaderElection(_logger.Object);

        Assert.True(sut.IsLeader);
    }

    [Fact]
    public void InstanceId_IsSetAndStable()
    {
        using var sut = new AlwaysLeaderElection(_logger.Object);

        Assert.NotEqual(Guid.Empty, sut.InstanceId);
        Assert.Equal(sut.InstanceId, sut.InstanceId);
    }

    [Fact]
    public async Task StartAsync_LogsDiscoverabilityHint()
    {
        using var sut = new AlwaysLeaderElection(_logger.Object);

        await sut.StartAsync(CancellationToken.None);

        _logger.Verify(
            x => x.Log(
                It.Is<LogLevel>(l => l == LogLevel.Information),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Leader election disabled")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutError()
    {
        using var sut = new AlwaysLeaderElection(_logger.Object);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void IsLeaderGauge_ReportsConstantOne_WithSameNameAndTagAsRedisLeaderElector()
    {
        using var sut = new AlwaysLeaderElection(_logger.Object);

        var values = new List<(int Value, string? InstanceId)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CommitCoordinatorWorker.MeterName
                    && instrument.Name == RedisLeaderElector.IsLeaderGaugeName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<int>((_, measurement, tags, _) =>
        {
            string? instanceId = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "instance_id" && tag.Value is string id)
                {
                    instanceId = id;
                }
            }

            // Other AlwaysLeaderElection/RedisLeaderElector instances left over from other tests
            // in the same process (each owns its own Meter, per the type doc) may still be
            // publishing on the same meter/gauge name — filter down to THIS test's instance.
            if (instanceId == sut.InstanceId.ToString())
            {
                values.Add((measurement, instanceId));
            }
        });

        listener.Start();
        listener.RecordObservableInstruments();

        var reading = Assert.Single(values);
        Assert.Equal(1, reading.Value);
        Assert.Equal(sut.InstanceId.ToString(), reading.InstanceId);
    }
}
