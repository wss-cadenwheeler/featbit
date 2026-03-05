using System.Text.Json;
using Api.Application.ControlPlane;
using Application.Caches;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Utils;
using Microsoft.Extensions.Logging;
using Moq;

namespace Api.UnitTests.Application.ControlPlane;

public class FeatureFlagChangeMessageHandlerTests
{
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<IMessageProducer> _producer = new();
    private readonly Mock<ILogger<FeatureFlagChangeMessageHandler>> _logger = new();

    private FeatureFlagChangeMessageHandler CreateSut()
        => new(_cache.Object, _producer.Object, _logger.Object);

    [Fact]
    public async Task HandleAsync_WhenDeserializesToNull_DoesNothing()
    {
        var sut = CreateSut();

        await sut.HandleAsync("null");

        _cache.Verify(x => x.UpsertFlagAsync(It.IsAny<FeatureFlag>()), Times.Never);
        _producer.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<FeatureFlag>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenValid_UpsertsAndPublishes()
    {
        var sut = CreateSut();

        var flag = new FeatureFlag();
        var payload = JsonSerializer.Serialize(flag, ReusableJsonSerializerOptions.Web);

        await sut.HandleAsync(payload);

        _cache.Verify(x => x.UpsertFlagAsync(It.IsAny<FeatureFlag>()), Times.Once);
        _producer.Verify(x => x.PublishAsync(Topics.FeatureFlagChange, It.IsAny<FeatureFlag>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenJsonInvalid_Rethrows()
    {
        var sut = CreateSut();

        await Assert.ThrowsAnyAsync<Exception>(() => sut.HandleAsync("{not valid json"));
    }
}