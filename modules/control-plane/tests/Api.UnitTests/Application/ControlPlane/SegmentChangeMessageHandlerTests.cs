using System.Collections.ObjectModel;
using Api.Application.ControlPlane;
using Application.Caches;
using Application.Segments;
using Application.Services;
using Domain.Segments;
using Microsoft.Extensions.Logging;
using Moq;

namespace Api.UnitTests.Application.ControlPlane;

public class SegmentChangeMessageHandlerTests
{
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<IFeatureFlagAppService> _ff = new();
    private readonly Mock<ISegmentMessageService> _segSvc = new();
    private readonly Mock<ILogger<SegmentChangeMessageHandler>> _logger = new();

    private SegmentChangeMessageHandler CreateSut()
        => new(_cache.Object, _ff.Object, _segSvc.Object, _logger.Object);

    [Fact]
    public async Task HandleAsync_WhenRequiredPropertiesMissing_Throws()
    {
        var sut = CreateSut();
        
        var payload = """{"anything":"else"}""";

        await Assert.ThrowsAsync<InvalidDataException>(() => sut.HandleAsync(payload));
    }

    [Fact]
    public async Task HandleAsync_WhenJsonInvalid_Rethrows()
    {
        var sut = CreateSut();

        await Assert.ThrowsAnyAsync<Exception>(() => sut.HandleAsync("{not valid json"));
    }

    [Fact]
    public async Task HandleAsync_WhenValidPayload_UpsertsSegment_AndPublishesForEachEnv()
    {
        _segSvc
            .Setup(x => x.GetAffectedFlagsAsync(It.IsAny<Guid>(), It.IsAny<OnSegmentChange>()))
            .ReturnsAsync(new Collection<FlagReference>());

        _segSvc
            .Setup(x => x.PublishSegmentChangeMessage(It.IsAny<Guid>(), It.IsAny<ICollection<FlagReference>>(),
                It.IsAny<Segment>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        var env1 = Guid.NewGuid();
        var env2 = Guid.NewGuid();

        var payload = $$"""
                        {
                          "segmentNonSpecific": {},
                          "envIds": ["{{env1}}","{{env2}}"],
                          "notification": {}
                        }
                        """;

        await sut.HandleAsync(payload);

        _cache.Verify(x => x.UpsertSegmentAsync(It.IsAny<ICollection<Guid>>(), It.IsAny<Segment>()), Times.Once);
        
        _ff.Verify(
            x => x.OnSegmentUpdatedAsync(It.IsAny<Segment>(), It.IsAny<Guid>(), It.IsAny<ICollection<FlagReference>>()),
            Times.Never);

        _segSvc.Verify(
            x => x.PublishSegmentChangeMessage(env1, It.IsAny<ICollection<FlagReference>>(), It.IsAny<Segment>()),
            Times.Once);
        _segSvc.Verify(
            x => x.PublishSegmentChangeMessage(env2, It.IsAny<ICollection<FlagReference>>(), It.IsAny<Segment>()),
            Times.Once);
    }
}