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
        var redisKey = RedisKeys.Heartbeat(healthMessage.PodId);

        var fields = new HashEntry[]
        {
            new("podId", healthMessage.PodId),
            new("timestamp", healthMessage.Timestamp.ToUnixTimeSeconds())
        };

        await Redis.HashSetAsync(redisKey, fields);
    }

    public async Task DeletePodConnection(Guid podId)
    {
        await Redis.KeyDeleteAsync(RedisKeys.Heartbeat(podId.ToString()));

        await DeletePodClients(podId);
    }

    public async Task<List<HealthMessage>> GetAllHealthMessages()
    {
        var keys = redis.Connection.GetServer(redis.Connection.GetEndPoints().First()).Keys(pattern: RedisKeys.GetAllHeartBeats);

        var values = await Redis.HashGetAllAsync(RedisKeys.Heartbeat("*"));

        return [
           ..values
        .Where(v => v.Value.HasValue)
        .Select(v => JsonSerializer.Deserialize<HealthMessage>(v.Value!))
        .OfType<HealthMessage>()
       ];
    }

    private async Task DeletePodClients(Guid heartbeatId)
    {
        var connectionKeys = redis.Connection.GetServer(redis.Connection.GetEndPoints().First()).Keys(pattern: RedisKeys.GetAllConnections).ToList();

        var droppedConnectionsTasks = connectionKeys.Select(async key => await Redis.HashGetAsync(key, "heartbeatId")).ToList();
        
        var connectionsToRemove = await Task.WhenAll(droppedConnectionsTasks);
            
        var connectionsToRemoveKeys = connectionKeys.Select((key, index) => new { Key = key, HeartbeatId = connectionsToRemove[index] })
            .Where(x => x.HeartbeatId.HasValue && x.HeartbeatId == heartbeatId.ToString())
            .Select(x => x.Key)
            .ToList();


        if (connectionsToRemoveKeys.Count > 0)
        {
            await Redis.KeyDeleteAsync(connectionsToRemoveKeys.ToArray());
        }
    }
}