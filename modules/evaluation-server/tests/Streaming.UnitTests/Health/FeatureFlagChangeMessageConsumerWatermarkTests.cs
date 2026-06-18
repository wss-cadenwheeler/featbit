using Microsoft.Extensions.Logging.Testing;
using Moq;
using Streaming.Connections;
using Streaming.Consumers;
using Streaming.Health;
using Streaming.Services;

namespace Streaming.UnitTests.Health;

public class FeatureFlagChangeMessageConsumerWatermarkTests
{
    private readonly AppliedWatermarkTracker _tracker = new();
    private readonly Mock<IConnectionManager> _connectionManager = new();
    private readonly Mock<IDataSyncService> _dataSyncService = new();

    private FeatureFlagChangeMessageConsumer CreateSut()
    {
        // No connections: isolates the watermark-tracking behaviour from the send path.
        _connectionManager
            .Setup(m => m.GetEnvConnections(It.IsAny<Guid>()))
            .Returns(Array.Empty<Connection>());

        return new FeatureFlagChangeMessageConsumer(
            _connectionManager.Object,
            _dataSyncService.Object,
            _tracker,
            new FakeLogger<FeatureFlagChangeMessageConsumer>());
    }

    private static string FlagMessage(Guid envId, string updatedAtIso) =>
        $$"""{"envId":"{{envId}}","key":"flag-1","updatedAt":"{{updatedAtIso}}"}""";

    [Fact]
    public async Task HandleAsync_RecordsWatermark_AsUpdatedAtUnixMs()
    {
        var sut = CreateSut();
        var envId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.Parse("2026-06-18T00:00:00Z");

        await sut.HandleAsync(FlagMessage(envId, "2026-06-18T00:00:00Z"), CancellationToken.None);

        Assert.Equal(updatedAt.ToUnixTimeMilliseconds(), _tracker.Snapshot()[envId]);
    }

    [Fact]
    public async Task HandleAsync_KeepsMaximum_AcrossMultipleVersions()
    {
        var sut = CreateSut();
        var envId = Guid.NewGuid();

        var older = DateTimeOffset.Parse("2026-06-18T00:00:00Z");
        var newer = DateTimeOffset.Parse("2026-06-18T01:00:00Z");

        // Apply newer first, then older — the tracker must keep the maximum.
        await sut.HandleAsync(FlagMessage(envId, "2026-06-18T01:00:00Z"), CancellationToken.None);
        await sut.HandleAsync(FlagMessage(envId, "2026-06-18T00:00:00Z"), CancellationToken.None);

        Assert.Equal(newer.ToUnixTimeMilliseconds(), _tracker.Snapshot()[envId]);
        Assert.NotEqual(older.ToUnixTimeMilliseconds(), _tracker.Snapshot()[envId]);
    }

    [Fact]
    public async Task HandleAsync_WithoutUpdatedAt_DoesNotThrowOrRecord()
    {
        var sut = CreateSut();
        var envId = Guid.NewGuid();

        await sut.HandleAsync($$"""{"envId":"{{envId}}","key":"flag-1"}""", CancellationToken.None);

        Assert.Empty(_tracker.Snapshot());
    }
}
