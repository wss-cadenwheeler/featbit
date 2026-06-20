using System.Text;
using Infrastructure.Caches.Redis;
using Infrastructure.Store;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Application.IntegrationTests.Store;

/// <summary>
/// D4 integration tests: <see cref="RedisStore"/> segment reads must honor the committed pointer
/// written by the back-end gated commit path (<c>featbit:segment-committed:{id}</c>), and fall back
/// to the legacy main key (<c>featbit:segment:{id}</c>) otherwise. Mirrors the D2 flag tests in
/// <see cref="RedisStoreCommittedPointerTests"/>.
///
/// Requires a throwaway Redis on port 6393:
///   docker run -d --rm -p 6393:6379 --name d4seg-redis redis:7-alpine
/// </summary>
public class RedisStoreSegmentCommittedPointerTests : IDisposable
{
    private const string ConnectionString = "localhost:6393,abortConnect=false,connectTimeout=2000";

    private readonly RedisClient _redisClient;
    private readonly IDatabase _db;
    private readonly RedisStore _sut;

    public RedisStoreSegmentCommittedPointerTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:ConnectionString"] = ConnectionString
            })
            .Build();

        _redisClient = new RedisClient(configuration);
        _db = _redisClient.GetDatabase();
        _sut = new RedisStore(_redisClient);
    }

    private static byte[] SegmentValue(string id, long ts) =>
        Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"ts\":{ts}}}");

    // A shared segment is serialized with an empty envId that GetSegmentsAsync replaces with the
    // actual envId at read time.
    private static byte[] SharedSegmentValue(string id, long ts) =>
        Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"envId\":\"\",\"ts\":{ts}}}");

    private async Task SeedIndexAsync(Guid envId, string id, long score)
    {
        await _db.SortedSetAddAsync(RedisKeys.SegmentIndex(envId), id, score);
    }

    [Fact]
    public async Task GetSegmentsAsync_ByEnv_ReturnsCommittedVersion_NotUncommittedStaged()
    {
        // Arrange — gated path: two staged versions, pointer at 2000, index score 2000.
        var envId = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();

        await _db.StringSetAsync(RedisKeys.SegmentVersion(id, 1000), SegmentValue(id, 1000));
        await _db.StringSetAsync(RedisKeys.SegmentVersion(id, 2000), SegmentValue(id, 2000));
        await _db.StringSetAsync(RedisKeys.SegmentCommittedPointer(id), "2000");
        await SeedIndexAsync(envId, id, 2000);

        // Act — should resolve the pointer to v2000.
        var committed = (await _sut.GetSegmentsAsync(envId, 0)).ToList();

        // Assert
        Assert.Single(committed);
        Assert.Equal(SegmentValue(id, 2000), committed[0]);

        // Arrange — stage a newer v3000 WITHOUT moving the pointer.
        await _db.StringSetAsync(RedisKeys.SegmentVersion(id, 3000), SegmentValue(id, 3000));

        // Act — must still return committed v2000, never the uncommitted staged v3000.
        var stillCommitted = (await _sut.GetSegmentsAsync(envId, 0)).ToList();

        // Assert
        Assert.Single(stillCommitted);
        Assert.Equal(SegmentValue(id, 2000), stillCommitted[0]);
    }

    [Fact]
    public async Task GetSegmentsAsync_ByEnv_FallsBackToMainKey_WhenNoPointer()
    {
        // Arrange — BestEffort path: only the legacy main key + index, no pointer.
        var envId = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();
        var mainValue = SegmentValue(id, 500);

        await _db.StringSetAsync(RedisKeys.Segment(id), mainValue);
        await SeedIndexAsync(envId, id, 500);

        // Act
        var result = (await _sut.GetSegmentsAsync(envId, 0)).ToList();

        // Assert — unchanged behavior: returns the main-key value.
        Assert.Single(result);
        Assert.Equal(mainValue, result[0]);
    }

    [Fact]
    public async Task GetSegmentsAsync_ByEnv_ReplacesEmptyEnvId_ForCommittedSharedSegment()
    {
        // Arrange — gated path with a shared segment (empty envId) as the committed version.
        var envId = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();

        await _db.StringSetAsync(RedisKeys.SegmentVersion(id, 2000), SharedSegmentValue(id, 2000));
        await _db.StringSetAsync(RedisKeys.SegmentCommittedPointer(id), "2000");
        await SeedIndexAsync(envId, id, 2000);

        // Act
        var result = (await _sut.GetSegmentsAsync(envId, 0)).ToList();

        // Assert — empty envId replaced with the actual envId on the committed versioned value.
        Assert.Single(result);
        var expected = Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"envId\":\"{envId}\",\"ts\":2000}}");
        Assert.Equal(expected, result[0]);
    }

    [Fact]
    public async Task GetSegmentsAsync_ByEnv_ReplacesEmptyEnvId_ForMainKeySharedSegment()
    {
        // Arrange — BestEffort path with a shared segment (empty envId) on the main key.
        var envId = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();

        await _db.StringSetAsync(RedisKeys.Segment(id), SharedSegmentValue(id, 500));
        await SeedIndexAsync(envId, id, 500);

        // Act
        var result = (await _sut.GetSegmentsAsync(envId, 0)).ToList();

        // Assert — empty envId replaced with the actual envId (unchanged behavior).
        Assert.Single(result);
        var expected = Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"envId\":\"{envId}\",\"ts\":500}}");
        Assert.Equal(expected, result[0]);
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsCommittedVersion_NotUncommittedStaged()
    {
        // Arrange
        var id = Guid.NewGuid().ToString();

        await _db.StringSetAsync(RedisKeys.SegmentVersion(id, 1000), SegmentValue(id, 1000));
        await _db.StringSetAsync(RedisKeys.SegmentVersion(id, 2000), SegmentValue(id, 2000));
        await _db.StringSetAsync(RedisKeys.SegmentCommittedPointer(id), "2000");
        await _db.StringSetAsync(RedisKeys.SegmentVersion(id, 3000), SegmentValue(id, 3000));

        // Act
        var result = await _sut.GetSegmentAsync(id);

        // Assert — committed v2000, not staged v3000.
        Assert.Equal(SegmentValue(id, 2000), result);
    }

    [Fact]
    public async Task GetSegmentAsync_FallsBackToMainKey_WhenNoPointer()
    {
        // Arrange
        var id = Guid.NewGuid().ToString();
        var mainValue = SegmentValue(id, 500);
        await _db.StringSetAsync(RedisKeys.Segment(id), mainValue);

        // Act
        var result = await _sut.GetSegmentAsync(id);

        // Assert
        Assert.Equal(mainValue, result);
    }

    public void Dispose()
    {
        _redisClient.Connection.Dispose();
    }
}
