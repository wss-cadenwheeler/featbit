using System.Text;
using Domain.Shared;
using Infrastructure.Caches.Redis;
using StackExchange.Redis;

namespace Infrastructure.Store;

public class RedisStore(IRedisClient redisClient) : IDbStore
{
    public string Name => Stores.Redis;

    private IDatabase Redis => redisClient.GetDatabase();

    public Task<bool> IsAvailableAsync() => redisClient.IsHealthyAsync();

    public async Task<IEnumerable<byte[]>> GetFlagsAsync(Guid envId, long timestamp)
    {
        // get flag ids from the index sorted set
        var index = RedisKeys.FlagIndex(envId);
        var ids = await Redis.SortedSetRangeByScoreAsync(index, timestamp, exclude: Exclude.Start);

        return await GetFlagsByIdsAsync(ids.Select(id => id.ToString()));
    }

    public async Task<IEnumerable<byte[]>> GetFlagsAsync(IEnumerable<string> ids)
    {
        return await GetFlagsByIdsAsync(ids);
    }

    /// <summary>
    /// Reads flag values honoring the committed pointer written by the back-end gated commit path.
    /// For each id: if the committed-pointer key <c>featbit:flag-committed:{id}</c> exists, the
    /// authoritative value is the versioned snapshot <c>featbit:flag:{id}:v{pointer}</c>; otherwise
    /// it falls back to the legacy single-value key <c>featbit:flag:{id}</c> (the BestEffort path,
    /// which writes the main key + index and never writes pointers). This auto-adapts to either
    /// mode without a mode flag. Pointer GETs and value GETs are each batched via
    /// <see cref="Task.WhenAll(System.Threading.Tasks.Task[])"/> to avoid serial round-trips.
    /// NOTE: segments are intentionally left unchanged here; that is tracked separately as D4.
    /// </summary>
    private async Task<IEnumerable<byte[]>> GetFlagsByIdsAsync(IEnumerable<string> ids)
    {
        var idList = ids.ToList();

        // batch the committed-pointer GETs
        var pointerTasks = idList.Select(id => Redis.StringGetAsync(RedisKeys.FlagCommittedPointer(id)));
        var pointers = await Task.WhenAll(pointerTasks);

        // resolve each id to its authoritative value key: versioned snapshot if a pointer exists,
        // otherwise the legacy main key (BestEffort fallback)
        var valueKeys = idList.Select((id, i) =>
        {
            var pointer = pointers[i];
            return pointer.HasValue
                ? RedisKeys.FlagVersion(id, (long)pointer)
                : RedisKeys.Flag(id);
        });

        // batch the value GETs
        var valueTasks = valueKeys.Select(key => Redis.StringGetAsync(key));
        var values = await Task.WhenAll(valueTasks);

        return values.Select(x => (byte[])x!);
    }

    public async Task<byte[]> GetSegmentAsync(string id)
    {
        var key = RedisKeys.Segment(id);
        var segment = await Redis.StringGetAsync(key);

        return (byte[])segment!;
    }

    public async Task<IEnumerable<byte[]>> GetSegmentsAsync(Guid envId, long timestamp)
    {
        // get segment keys
        var index = RedisKeys.SegmentIndex(envId);
        var ids = await Redis.SortedSetRangeByScoreAsync(index, timestamp, exclude: Exclude.Start);
        var keys = ids.Select(id => RedisKeys.Segment(id!));

        // get segments
        var tasks = keys.Select(key => Redis.StringGetAsync(key));
        var values = await Task.WhenAll(tasks);

        // for shared segments, replace empty envId with actual envId
        const string emptyEnvId = "\"envId\":\"\",";

        List<byte[]> jsonBytes = [];
        foreach (var value in values)
        {
            var strValue = (string)value!;
            if (strValue.Contains(emptyEnvId))
            {
                var newStrValue = strValue.Replace(emptyEnvId, $"\"envId\":\"{envId}\",");
                jsonBytes.Add(Encoding.UTF8.GetBytes(newStrValue));
            }
            else
            {
                jsonBytes.Add((byte[])value!);
            }
        }

        return jsonBytes;
    }

    public async Task<Secret?> GetSecretAsync(string secretString)
    {
        var key = RedisKeys.Secret(secretString);
        if (!await Redis.KeyExistsAsync(key))
        {
            return null;
        }

        var entries = await Redis.HashGetAsync(key, new RedisValue[] { "type", "projectKey", "envId", "envKey" });
        return new Secret(
            type: entries[0].ToString(),
            entries[1].ToString(),
            Guid.Parse(entries[2].ToString()),
            entries[3].ToString()
        );
    }
}