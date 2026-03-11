using Api.Application.Admin;
using Domain.Messages;
using Microsoft.Extensions.Logging;
using Moq;

namespace Api.UnitTests.Application.Admin;

public class PushFullSyncHandlerTests
{
    private readonly Mock<IMessageProducer> _producer = new();
    private readonly Mock<ILogger<PushFullSyncHandler>> _logger = new();

    private PushFullSyncHandler CreateSut()
        => new(_producer.Object, _logger.Object);

    [Fact]
    public async Task Handle_WhenPublishSucceeds_ReturnsTrue_AndPublishesToExpectedTopic()
    {
        var sut = CreateSut();

        _producer
            .Setup(x => x.PublishAsync(
                ControlPlaneTopics.PushFullSyncChange,
                It.IsAny<object>()))
            .Returns(Task.CompletedTask);

        var result = await sut.Handle(new PushFullSync(), CancellationToken.None);

        Assert.True(result);

        _producer.Verify(x => x.PublishAsync(
            ControlPlaneTopics.PushFullSyncChange,
            It.IsAny<object>()), Times.Once);

        _logger.Verify(
            x => x.Log(
                It.Is<LogLevel>(l => l == LogLevel.Error),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_WhenPublishThrows_LogsError_AndReturnsFalse()
    {
        var sut = CreateSut();

        var ex = new InvalidOperationException("boom");

        _producer
            .Setup(x => x.PublishAsync(
                ControlPlaneTopics.PushFullSyncChange,
                It.IsAny<object>()))
            .ThrowsAsync(ex);

        var result = await sut.Handle(new PushFullSync(), CancellationToken.None);

        Assert.False(result);

        _producer.Verify(x => x.PublishAsync(
            ControlPlaneTopics.PushFullSyncChange,
            It.IsAny<object>()), Times.Once);

        _logger.Verify(
            x => x.Log(
                It.Is<LogLevel>(l => l == LogLevel.Error),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(e => ReferenceEquals(e, ex)),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}