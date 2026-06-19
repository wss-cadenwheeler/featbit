using Api.Configuration;
using Api.Health;
using Domain.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Streaming.Health;

namespace Application.IntegrationTests.Health;

/// <summary>
/// Pure unit tests for the heartbeat payload construction (no infra). Exercises
/// <see cref="HeartbeatService.BuildHeartbeat"/> and the DcId/Region config accessors.
/// </summary>
public class HeartbeatServiceTests
{
    private sealed class NoopMessageProducer : IMessageProducer
    {
        public Task PublishAsync<TMessage>(string topic, TMessage? message) where TMessage : class
            => Task.CompletedTask;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static HeartbeatService CreateSut(IConfiguration configuration, IAppliedWatermarkTracker tracker)
        => new(
            new NoopMessageProducer(),
            NullLogger<HeartbeatService>.Instance,
            configuration,
            tracker);

    [Fact]
    public void BuildHeartbeat_PopulatesDcIdAndRegion_FromConfig()
    {
        var config = Config(
            ("ControlPlane:DcId", "dc-west"),
            ("ControlPlane:Region", "us-west-2"));
        var sut = CreateSut(config, new AppliedWatermarkTracker());

        var heartbeat = sut.BuildHeartbeat();

        Assert.Equal("dc-west", heartbeat.DcId);
        Assert.Equal("us-west-2", heartbeat.Region);
    }

    [Fact]
    public void BuildHeartbeat_DcIdAndRegion_AreNull_WhenUnset()
    {
        var sut = CreateSut(Config(), new AppliedWatermarkTracker());

        var heartbeat = sut.BuildHeartbeat();

        Assert.Null(heartbeat.DcId);
        Assert.Null(heartbeat.Region);
    }

    [Fact]
    public void BuildHeartbeat_DcIdAndRegion_AreNull_WhenWhitespace()
    {
        var config = Config(
            ("ControlPlane:DcId", "   "),
            ("ControlPlane:Region", ""));
        var sut = CreateSut(config, new AppliedWatermarkTracker());

        var heartbeat = sut.BuildHeartbeat();

        Assert.Null(heartbeat.DcId);
        Assert.Null(heartbeat.Region);
    }

    [Fact]
    public void BuildHeartbeat_IncludesAppliedWatermarks_FromTracker()
    {
        var tracker = new AppliedWatermarkTracker();
        var env1 = Guid.NewGuid();
        var env2 = Guid.NewGuid();
        tracker.Update(env1, 100);
        tracker.Update(env1, 50);   // ignored: lower
        tracker.Update(env2, 300);

        var sut = CreateSut(Config(), tracker);

        var heartbeat = sut.BuildHeartbeat();

        Assert.NotNull(heartbeat.AppliedWatermarks);
        Assert.Equal(100, heartbeat.AppliedWatermarks![env1]);
        Assert.Equal(300, heartbeat.AppliedWatermarks![env2]);
    }

    [Fact]
    public void BuildHeartbeat_AppliedWatermarks_IsNull_WhenNothingApplied()
    {
        var sut = CreateSut(Config(), new AppliedWatermarkTracker());

        var heartbeat = sut.BuildHeartbeat();

        Assert.Null(heartbeat.AppliedWatermarks);
    }

    [Fact]
    public void BuildHeartbeat_AlwaysCarries_PodIdAndTimestamp()
    {
        var sut = CreateSut(Config(), new AppliedWatermarkTracker());

        var heartbeat = sut.BuildHeartbeat();

        Assert.False(string.IsNullOrEmpty(heartbeat.PodId));
        Assert.NotEqual(default, heartbeat.Timestamp);
    }
}
