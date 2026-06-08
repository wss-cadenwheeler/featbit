using System.Text.Json;
using Api.Application.ControlPlane;
using Application.Caches;
using Domain.Health;
using Microsoft.Extensions.Logging;
using Moq;

namespace Api.UnitTests.Application.ControlPlane;

public class HeartbeatMessageHandlerTests
{
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<ILogger<HeartbeatMessageHandler>> _logger = new();

    private HeartbeatMessageHandler CreateSut()
        => new(_cache.Object, _logger.Object);

    [Fact]
    public async Task HandleAsync_WhenValid_CallsUpsertPodHeartbeat()
    {
        var sut = CreateSut();
        var msg = new HealthMessage
        {
            PodId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow
        };
        var payload = JsonSerializer.Serialize(msg);

        await sut.HandleAsync(payload);

        _cache.Verify(x => x.UpsertPodHeartbeat(It.Is<HealthMessage>(m =>
            m.PodId == msg.PodId && m.Timestamp == msg.Timestamp)), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenMessageMissingPodId_DoesNotCallCache()
    {
        var sut = CreateSut();
        var payload = JsonSerializer.Serialize(new HealthMessage
        {
            PodId = "",
            Timestamp = DateTimeOffset.UtcNow
        });

        await sut.HandleAsync(payload);

        _cache.Verify(x => x.UpsertPodHeartbeat(It.IsAny<HealthMessage>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenTimestampDefault_DoesNotCallCache()
    {
        var sut = CreateSut();
        var payload = JsonSerializer.Serialize(new HealthMessage
        {
            PodId = Guid.NewGuid().ToString(),
            Timestamp = default
        });

        await sut.HandleAsync(payload);

        _cache.Verify(x => x.UpsertPodHeartbeat(It.IsAny<HealthMessage>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenJsonInvalid_DoesNotThrowAndDoesNotCallCache()
    {
        var sut = CreateSut();

        await sut.HandleAsync("{not valid json");

        _cache.Verify(x => x.UpsertPodHeartbeat(It.IsAny<HealthMessage>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenJsonIsNullLiteral_DoesNotCallCache()
    {
        var sut = CreateSut();

        await sut.HandleAsync("null");

        _cache.Verify(x => x.UpsertPodHeartbeat(It.IsAny<HealthMessage>()), Times.Never);
    }
}
