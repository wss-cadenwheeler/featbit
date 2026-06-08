using System.Text.Json;
using Api.Application.ControlPlane;
using Application.Caches;
using Application.FeatureFlags;
using Domain.AuditLogs;
using Domain.FeatureFlags;
using Domain.Messages;
using Domain.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Api.UnitTests.Application.ControlPlane;

public class FeatureFlagChangeMessageHandlerTests
{
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<IMessageProducer> _producer = new();
    private readonly Mock<ILogger<FeatureFlagChangeMessageHandler>> _logger = new();
    private readonly Mock<IConfiguration> _config = new();

    private FeatureFlagChangeMessageHandler CreateSut()
        => new(_cache.Object, _producer.Object, _logger.Object, _config.Object);

    [Fact]
    public async Task HandleAsync_WhenValid_UpsertsAndPublishes()
    {
        _config
            .Setup(x => x.GetSection(It.IsAny<string>()).Value)
            .Returns("us-east-1");
        
        var sut = CreateSut();
        var flag = new FeatureFlag();
        var onFeatureFlagChanged = new OnFeatureFlagChanged(flag, "op", new DataChange(), Guid.NewGuid(), "user");
        
        var message = new
        {
            notification = onFeatureFlagChanged,
            region = "us-east-1"
        };
        

        await sut.HandleAsync(JsonSerializer.Serialize(message, ReusableJsonSerializerOptions.Web));

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