using Api.Infrastructure.MQ;
using Microsoft.Extensions.Configuration;

namespace Api.UnitTests.Infrastructure.MQ;

/// <summary>
/// #100 unit coverage for <see cref="MqServiceCollectionExtensions.ResolveConsumerGroupId"/>: the
/// pure helper that suffixes the shipped default Kafka consumer group.id with the local DC
/// identity so two control planes sharing a Kafka broker don't collide on a single static
/// group.id (only one DC would process flag/segment changes and heartbeats while the other
/// silently idles). Exercised directly (no DI / real Kafka needed).
/// </summary>
public class MqServiceCollectionExtensionsTests
{
    private static IConfiguration CreateConfiguration(string? dcId)
    {
        var data = new Dictionary<string, string?>();
        if (dcId != null)
        {
            data["Redis:Instances:0:DcId"] = dcId;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();
    }

    [Fact]
    public void DefaultGroupId_WithDcId_IsSuffixedWithDcId()
    {
        var configuration = CreateConfiguration("west");

        var result = MqServiceCollectionExtensions.ResolveConsumerGroupId(
            configuration, MqServiceCollectionExtensions.DefaultConsumerGroupId);

        Assert.Equal("featbit-control-plane-west", result);
    }

    [Fact]
    public void CustomGroupId_IsNeverModified_EvenWithDcIdConfigured()
    {
        var configuration = CreateConfiguration("west");

        var result = MqServiceCollectionExtensions.ResolveConsumerGroupId(
            configuration, "my-custom-group-id");

        Assert.Equal("my-custom-group-id", result);
    }

    [Fact]
    public void DefaultGroupId_WithoutDcId_IsUnchanged()
    {
        var configuration = CreateConfiguration(null);

        var result = MqServiceCollectionExtensions.ResolveConsumerGroupId(
            configuration, MqServiceCollectionExtensions.DefaultConsumerGroupId);

        Assert.Equal(MqServiceCollectionExtensions.DefaultConsumerGroupId, result);
    }

    [Fact]
    public void DefaultGroupId_WithEmptyDcId_IsUnchanged()
    {
        var configuration = CreateConfiguration("");

        var result = MqServiceCollectionExtensions.ResolveConsumerGroupId(
            configuration, MqServiceCollectionExtensions.DefaultConsumerGroupId);

        Assert.Equal(MqServiceCollectionExtensions.DefaultConsumerGroupId, result);
    }

    [Fact]
    public void CustomGroupId_WithoutDcId_IsUnchanged()
    {
        var configuration = CreateConfiguration(null);

        var result = MqServiceCollectionExtensions.ResolveConsumerGroupId(
            configuration, "featbit-control-plane-legacy");

        Assert.Equal("featbit-control-plane-legacy", result);
    }
}
