using Api.Application.ControlPlane;
using Application.Caches;
using Domain.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Api.UnitTests.Application.ControlPlane;

public class PodHealthCheckerTests
{
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<ILogger<PodHealthChecker>> _logger = new();

    private static IConfiguration BuildConfig(int timeoutSeconds, int checkIntervalSeconds)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PodHealth:TimeoutInSeconds"] = timeoutSeconds.ToString(),
                ["PodHealth:CheckIntervalInSeconds"] = checkIntervalSeconds.ToString()
            })
            .Build();
    }

    private async Task RunOnceAsync(PodHealthChecker sut)
    {
        using var cts = new CancellationTokenSource();
        var task = sut.StartAsync(cts.Token);
        // Give the loop a moment to execute one iteration before cancelling.
        await Task.Delay(100);
        await cts.CancelAsync();
        await sut.StopAsync(CancellationToken.None);
        await task;
    }

    [Fact]
    public async Task ExecuteAsync_WhenPodHeartbeatPastTimeout_DeletesPodConnection()
    {
        var deadPodId = Guid.NewGuid();
        _cache.Setup(c => c.GetAllHealthMessages())
            .ReturnsAsync(new List<HealthMessage>
            {
                new() { PodId = deadPodId.ToString(), Timestamp = DateTimeOffset.UtcNow.AddSeconds(-300) }
            });

        var sut = new PodHealthChecker(_cache.Object, _logger.Object, BuildConfig(60, 60));

        await RunOnceAsync(sut);

        _cache.Verify(c => c.DeletePodConnection(deadPodId), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPodHeartbeatWithinTimeout_DoesNotDeletePodConnection()
    {
        var livePodId = Guid.NewGuid();
        _cache.Setup(c => c.GetAllHealthMessages())
            .ReturnsAsync(new List<HealthMessage>
            {
                new() { PodId = livePodId.ToString(), Timestamp = DateTimeOffset.UtcNow }
            });

        var sut = new PodHealthChecker(_cache.Object, _logger.Object, BuildConfig(60, 60));

        await RunOnceAsync(sut);

        _cache.Verify(c => c.DeletePodConnection(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPodIdIsInvalidGuid_SkipsWithoutCrashing()
    {
        _cache.Setup(c => c.GetAllHealthMessages())
            .ReturnsAsync(new List<HealthMessage>
            {
                new() { PodId = "not-a-guid", Timestamp = DateTimeOffset.UtcNow.AddSeconds(-300) }
            });

        var sut = new PodHealthChecker(_cache.Object, _logger.Object, BuildConfig(60, 60));

        await RunOnceAsync(sut);

        _cache.Verify(c => c.DeletePodConnection(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGetAllHealthMessagesThrows_DoesNotPropagate()
    {
        _cache.Setup(c => c.GetAllHealthMessages())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = new PodHealthChecker(_cache.Object, _logger.Object, BuildConfig(60, 60));

        // Should not throw; failure inside the loop is logged and the next iteration is attempted.
        await RunOnceAsync(sut);
    }
}
