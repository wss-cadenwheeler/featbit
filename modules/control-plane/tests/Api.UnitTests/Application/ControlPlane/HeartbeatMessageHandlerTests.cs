using System.Text.Json;
using Api.Application.ControlPlane;
using Application.Caches;
using Application.ControlPlane;
using Domain.ControlPlane;
using Domain.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Api.UnitTests.Application.ControlPlane;

public class HeartbeatMessageHandlerTests
{
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<ILogger<HeartbeatMessageHandler>> _logger = new();
    private readonly Mock<ILeaseStore> _leaseStore = new();

    private HeartbeatMessageHandler CreateSut(IConfiguration? configuration = null)
        => new(_cache.Object, _logger.Object, _leaseStore.Object, configuration ?? BuildConfig());

    private static IConfiguration BuildConfig(
        string? consistencyMode = null, string? leaseTtlSeconds = null)
    {
        var values = new Dictionary<string, string?>();
        if (consistencyMode is not null)
        {
            values["ControlPlane:ConsistencyMode"] = consistencyMode;
        }
        if (leaseTtlSeconds is not null)
        {
            values["ControlPlane:LeaseTtlSeconds"] = leaseTtlSeconds;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

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

    [Fact]
    public async Task HandleAsync_WhenPayloadUsesCamelCase_CallsUpsertPodHeartbeat()
    {
        // Eval-server publishes camelCase JSON; the handler must accept it.
        var sut = CreateSut();
        var podId = Guid.NewGuid().ToString();
        var timestamp = DateTimeOffset.UtcNow;
        var payload =
            $"{{\"podId\":\"{podId}\",\"timestamp\":\"{timestamp:O}\"}}";

        await sut.HandleAsync(payload);

        _cache.Verify(x => x.UpsertPodHeartbeat(It.Is<HealthMessage>(m =>
            m.PodId == podId && m.Timestamp == timestamp)), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenBestEffort_DoesNotUpsertLease()
    {
        // BestEffort is the default; no lease should be written.
        var sut = CreateSut();
        var payload = JsonSerializer.Serialize(new HealthMessage
        {
            PodId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow
        });

        await sut.HandleAsync(payload);

        _cache.Verify(x => x.UpsertPodHeartbeat(It.IsAny<HealthMessage>()), Times.Once);
        _leaseStore.Verify(x => x.UpsertLeaseAsync(It.IsAny<DcLease>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenBestEffortExplicit_DoesNotUpsertLease()
    {
        var sut = CreateSut(BuildConfig(consistencyMode: "BestEffort"));
        var payload = JsonSerializer.Serialize(new HealthMessage
        {
            PodId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow
        });

        await sut.HandleAsync(payload);

        _leaseStore.Verify(x => x.UpsertLeaseAsync(It.IsAny<DcLease>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenGatedCommit_UpsertsLeaseWithDefaultTtl()
    {
        var sut = CreateSut(BuildConfig(consistencyMode: "GatedCommit"));
        var timestamp = DateTimeOffset.UtcNow;
        var watermarks = new Dictionary<Guid, long> { [Guid.NewGuid()] = 42 };
        var msg = new HealthMessage
        {
            PodId = Guid.NewGuid().ToString(),
            Timestamp = timestamp,
            Region = "west",
            DcId = "dc-1",
            AppliedWatermarks = watermarks
        };
        var payload = JsonSerializer.Serialize(msg);

        await sut.HandleAsync(payload);

        _cache.Verify(x => x.UpsertPodHeartbeat(It.IsAny<HealthMessage>()), Times.Once);
        _leaseStore.Verify(x => x.UpsertLeaseAsync(It.Is<DcLease>(l =>
            l.DcId == "dc-1" &&
            l.Region == "west" &&
            l.LastHeartbeatAt == timestamp &&
            l.LeaseExpiresAt == timestamp.AddSeconds(15) &&
            l.AppliedWatermarks.Count == 1)), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenGatedCommitWithCustomTtl_UsesConfiguredTtl()
    {
        var sut = CreateSut(BuildConfig(consistencyMode: "GatedCommit", leaseTtlSeconds: "30"));
        var timestamp = DateTimeOffset.UtcNow;
        var msg = new HealthMessage
        {
            PodId = Guid.NewGuid().ToString(),
            Timestamp = timestamp,
            Region = "east",
            DcId = "dc-2"
        };
        var payload = JsonSerializer.Serialize(msg);

        await sut.HandleAsync(payload);

        _leaseStore.Verify(x => x.UpsertLeaseAsync(It.Is<DcLease>(l =>
            l.DcId == "dc-2" &&
            l.LeaseExpiresAt == timestamp.AddSeconds(30))), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenGatedCommitAndDcIdNull_FallsBackToPodId()
    {
        var sut = CreateSut(BuildConfig(consistencyMode: "GatedCommit"));
        var podId = Guid.NewGuid().ToString();
        var msg = new HealthMessage
        {
            PodId = podId,
            Timestamp = DateTimeOffset.UtcNow,
            Region = "west"
            // DcId omitted (null)
        };
        var payload = JsonSerializer.Serialize(msg);

        await sut.HandleAsync(payload);

        _leaseStore.Verify(x => x.UpsertLeaseAsync(It.Is<DcLease>(l =>
            l.DcId == podId)), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenGatedCommitAndWatermarksNull_UsesEmptyDictionary()
    {
        var sut = CreateSut(BuildConfig(consistencyMode: "GatedCommit"));
        var msg = new HealthMessage
        {
            PodId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            DcId = "dc-3"
            // AppliedWatermarks omitted (null)
        };
        var payload = JsonSerializer.Serialize(msg);

        await sut.HandleAsync(payload);

        _leaseStore.Verify(x => x.UpsertLeaseAsync(It.Is<DcLease>(l =>
            l.AppliedWatermarks != null && l.AppliedWatermarks.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenGatedCommitButInvalidMessage_DoesNotUpsertLease()
    {
        var sut = CreateSut(BuildConfig(consistencyMode: "GatedCommit"));
        var payload = JsonSerializer.Serialize(new HealthMessage
        {
            PodId = "",
            Timestamp = DateTimeOffset.UtcNow
        });

        await sut.HandleAsync(payload);

        _leaseStore.Verify(x => x.UpsertLeaseAsync(It.IsAny<DcLease>()), Times.Never);
    }
}
