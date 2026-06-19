using System.Text.Json;
using Application.Caches;
using Domain.Connections;
using Domain.Environments;
using Domain.FeatureFlags;
using Domain.Health;
using Domain.Segments;
using Domain.Workspaces;
using StackExchange.Redis;

namespace Infrastructure.Caches.Redis;

public class RedisCacheService(IRedisClient redis) : ICacheService
{
    private IDatabase Redis => redis.GetDatabase();

    public async Task UpsertFlagAsync(FeatureFlag flag)
    {
        // upsert flag
        var cache = RedisCaches.Flag(flag);
        await Redis.StringSetAsync(cache.Key, cache.Value);

        // upsert index
        var index = RedisCaches.FlagIndex(flag);
        await Redis.SortedSetAddAsync(index.Key, index.Member, index.Score);
    }

    /// <summary>
    /// Stages a new flag version (B1 stage/commit storage). Writes ONLY the versioned value key
    /// <c>featbit:flag:{id}:v{ts}</c>; it does NOT move the committed pointer and does NOT touch
    /// the env flag sorted-set index. The previously committed value therefore stays readable
    /// until <see cref="CommitFlagAsync"/> is called.
    /// </summary>
    public async Task StageFlagAsync(FeatureFlag flag, long ts)
    {
        var staged = RedisCaches.FlagStaged(flag, ts);
        await Redis.StringSetAsync(staged.Key, staged.Value);
    }

    /// <summary>
    /// Commits a previously staged flag version (B1 stage/commit storage). Sets the committed
    /// pointer <c>featbit:flag-committed:{id}</c> to <paramref name="ts"/> AND advances the env
    /// flag sorted-set index score to <paramref name="ts"/> (mirroring <see cref="UpsertFlagAsync"/>'s
    /// index logic), making the staged version the authoritative committed value.
    /// </summary>
    public async Task CommitFlagAsync(Guid envId, string flagId, long ts)
    {
        // flip the committed pointer to the staged version
        var pointerKey = RedisCaches.FlagCommittedPointer(Guid.Parse(flagId));
        await Redis.StringSetAsync(pointerKey, ts);

        // advance the env flag index score (mirror UpsertFlag index logic)
        var indexKey = RedisKeys.FlagIndex(envId);
        await Redis.SortedSetAddAsync(indexKey, flagId, ts);
    }

    /// <summary>
    /// Probes whether this Redis holds the staged version value key
    /// <c>featbit:flag:{id}:v{ts}</c> (B1 stage/commit storage).
    /// </summary>
    public Task<bool> HasStagedFlagAsync(Guid id, long ts)
    {
        return Redis.KeyExistsAsync(RedisCaches.FlagVersion(id, ts));
    }

    public async Task DeleteFlagAsync(Guid envId, Guid flagId)
    {
        // delete cache
        var cacheKey = RedisKeys.Flag(flagId);
        await Redis.KeyDeleteAsync(cacheKey);

        // delete index
        var index = RedisKeys.FlagIndex(envId);
        await Redis.SortedSetRemoveAsync(index, flagId.ToString());
    }

    public async Task UpsertSegmentAsync(ICollection<Guid> envIds, Segment segment)
    {
        // upsert cache
        var cache = RedisCaches.Segment(segment);
        await Redis.StringSetAsync(cache.Key, cache.Value);

        // upsert index
        foreach (var envId in envIds)
        {
            var index = RedisCaches.SegmentIndex(envId, segment);
            await Redis.SortedSetAddAsync(index.Key, index.Member, index.Score);
        }
    }

    /// <summary>
    /// Stages a new segment version (B2 stage/commit storage, mirroring <see cref="StageFlagAsync"/>).
    /// Writes ONLY the versioned value key <c>featbit:segment:{id}:v{ts}</c>; it does NOT move the
    /// committed pointer and does NOT touch any env segment sorted-set index. The previously
    /// committed value therefore stays readable until <see cref="CommitSegmentAsync"/> is called.
    /// </summary>
    public async Task StageSegmentAsync(Segment segment, long ts)
    {
        var staged = RedisCaches.SegmentStaged(segment, ts);
        await Redis.StringSetAsync(staged.Key, staged.Value);
    }

    /// <summary>
    /// Commits a previously staged segment version (B2 stage/commit storage, mirroring
    /// <see cref="CommitFlagAsync"/>). Sets the committed pointer
    /// <c>featbit:segment-committed:{id}</c> to <paramref name="ts"/> AND advances the segment
    /// sorted-set index score to <paramref name="ts"/> for each env in <paramref name="envIds"/>
    /// (segments can belong to multiple envs, mirroring <see cref="UpsertSegmentAsync"/>'s index
    /// logic), making the staged version the authoritative committed value.
    /// </summary>
    public async Task CommitSegmentAsync(ICollection<Guid> envIds, string segmentId, long ts)
    {
        // flip the committed pointer to the staged version
        var pointerKey = RedisCaches.SegmentCommittedPointer(Guid.Parse(segmentId));
        await Redis.StringSetAsync(pointerKey, ts);

        // advance the segment index score for each env (mirror UpsertSegment index logic)
        foreach (var envId in envIds)
        {
            var indexKey = RedisKeys.SegmentIndex(envId);
            await Redis.SortedSetAddAsync(indexKey, segmentId, ts);
        }
    }

    public async Task DeleteSegmentAsync(ICollection<Guid> envIds, Guid segmentId)
    {
        // delete cache
        var cacheKey = RedisKeys.Segment(segmentId);
        await Redis.KeyDeleteAsync(cacheKey);

        // delete index
        foreach (var envId in envIds)
        {
            var index = RedisKeys.SegmentIndex(envId);
            await Redis.SortedSetRemoveAsync(index, segmentId.ToString());
        }
    }

    public async Task UpsertLicenseAsync(Workspace workspace)
    {
        var key = RedisKeys.License(workspace.Id);
        var value = workspace.License;

        await Redis.StringSetAsync(key, value);
    }

    public async Task UpsertSecretAsync(ResourceDescriptor resourceDescriptor, Secret secret)
    {
        var key = RedisKeys.Secret(secret.Value);

        var organization = resourceDescriptor.Organization;
        var project = resourceDescriptor.Project;
        var environment = resourceDescriptor.Environment;

        var fields = new HashEntry[]
        {
            new("type", secret.Type),
            new("organizationId", organization.Id.ToString()),
            new("organizationKey", organization.Key),
            new("projectId", project.Id.ToString()),
            new("projectKey", project.Key),
            new("envId", environment.Id.ToString()),
            new("envKey", environment.Key)
        };

        await Redis.HashSetAsync(key, fields);
    }

    public async Task DeleteSecretAsync(Secret secret)
    {
        var key = RedisKeys.Secret(secret.Value);

        await Redis.KeyDeleteAsync(key);
    }

    public async Task<string> GetOrSetLicenseAsync(Guid workspaceId, Func<Task<string>> licenseGetter)
    {
        var key = RedisKeys.License(workspaceId);
        if (await Redis.KeyExistsAsync(key))
        {
            var value = await Redis.StringGetAsync(key);
            return value.ToString();
        }

        var license = await licenseGetter();
        await Redis.StringSetAsync(key, license);
        return license;
    }

    public async Task UpsertConnectionMadeAsync(ConnectionMessage connectionMessage)
    {
        var fields = new HashEntry[]
        {
            new("id", connectionMessage.Id),
            new("envId", connectionMessage.EnvId.ToString()),
            new("secret", connectionMessage.Secret),
            new("heartbeatId", connectionMessage.HeartbeatId)
        };

        await Redis.HashSetAsync(RedisKeys.Connection(connectionMessage.Id), fields);
    }

    public async Task DeleteConnectionMadeAsync(ConnectionMessage connectionMessage)
    {
        await Redis.KeyDeleteAsync(RedisKeys.Connection(connectionMessage.Id));
    }

    public async Task UpsertPodHeartbeat(HealthMessage healthMessage)
    {
        var value = JsonSerializer.Serialize(healthMessage);
        await Redis.HashSetAsync(RedisKeys.Heartbeats, healthMessage.PodId, value);
    }

    public async Task DeletePodConnection(Guid podId)
    {
        await Redis.HashDeleteAsync(RedisKeys.Heartbeats, podId.ToString());

        await DeletePodClients(podId);
    }

    public async Task<List<HealthMessage>> GetAllHealthMessages()
    {
        var entries = await Redis.HashGetAllAsync(RedisKeys.Heartbeats);

        var result = new List<HealthMessage>(entries.Length);
        foreach (var entry in entries)
        {
            if (!entry.Value.HasValue)
            {
                continue;
            }

            var message = JsonSerializer.Deserialize<HealthMessage>((string)entry.Value!);
            if (message is not null)
            {
                result.Add(message);
            }
        }

        return result;
    }

    private async Task DeletePodClients(Guid heartbeatId)
    {
        var heartbeatIdString = heartbeatId.ToString();
        var connectionKeys = new List<RedisKey>();

        foreach (var endPoint in redis.Connection.GetEndPoints())
        {
            var server = redis.Connection.GetServer(endPoint);
            if (server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(pattern: RedisKeys.ConnectionPattern, pageSize: 250))
            {
                connectionKeys.Add(key);
            }
        }

        if (connectionKeys.Count == 0)
        {
            return;
        }

        var heartbeatLookups = connectionKeys
            .Select(key => Redis.HashGetAsync(key, "heartbeatId"))
            .ToArray();

        var heartbeatValues = await Task.WhenAll(heartbeatLookups);

        var connectionsToRemove = connectionKeys
            .Zip(heartbeatValues, (key, value) => (Key: key, Value: value))
            .Where(x => x.Value.HasValue && x.Value == heartbeatIdString)
            .Select(x => x.Key)
            .ToArray();

        if (connectionsToRemove.Length > 0)
        {
            await Redis.KeyDeleteAsync(connectionsToRemove);
        }
    }
}