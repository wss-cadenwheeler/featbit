using Api.Infrastructure.Caches;
using Application.Caches;
using Domain.Health;
using Microsoft.Extensions.Logging;
using Moq;

namespace Api.UnitTests.Application.Caches;

public class CompositeRedisCacheServiceTests
{
    private readonly Mock<ICacheService> _local = new();
    private readonly Mock<ICacheService> _remote = new();
    private readonly Mock<ILogger<CompositeRedisCacheService>> _logger = new();

    private CompositeRedisCacheService CreateSut()
        => new(new[] { _local.Object, _remote.Object }, _logger.Object);

    [Fact]
    public async Task UpsertPodHeartbeat_WritesToLocalInstanceOnly()
    {
        var sut = CreateSut();
        var msg = new HealthMessage
        {
            PodId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow
        };

        await sut.UpsertPodHeartbeat(msg);

        _local.Verify(s => s.UpsertPodHeartbeat(msg), Times.Once);
        _remote.Verify(s => s.UpsertPodHeartbeat(It.IsAny<HealthMessage>()), Times.Never);
    }

    [Fact]
    public async Task UpsertPodHeartbeat_WhenLocalThrows_DoesNotBubbleAndDoesNotHitRemote()
    {
        _local.Setup(s => s.UpsertPodHeartbeat(It.IsAny<HealthMessage>()))
              .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = CreateSut();

        await sut.UpsertPodHeartbeat(new HealthMessage
        {
            PodId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow
        });

        _remote.Verify(s => s.UpsertPodHeartbeat(It.IsAny<HealthMessage>()), Times.Never);
    }

    [Fact]
    public async Task GetAllHealthMessages_ReadsFromLocalInstanceOnly()
    {
        var expected = new List<HealthMessage>
        {
            new() { PodId = Guid.NewGuid().ToString(), Timestamp = DateTimeOffset.UtcNow }
        };
        _local.Setup(s => s.GetAllHealthMessages()).ReturnsAsync(expected);

        var sut = CreateSut();

        var actual = await sut.GetAllHealthMessages();

        Assert.Same(expected, actual);
        _remote.Verify(s => s.GetAllHealthMessages(), Times.Never);
    }

    [Fact]
    public async Task DeletePodConnection_BroadcastsToAllInstances()
    {
        var podId = Guid.NewGuid();
        var sut = CreateSut();

        await sut.DeletePodConnection(podId);

        _local.Verify(s => s.DeletePodConnection(podId), Times.Once);
        _remote.Verify(s => s.DeletePodConnection(podId), Times.Once);
    }
}
