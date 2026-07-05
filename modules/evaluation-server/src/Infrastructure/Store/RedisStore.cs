using System.Text;
using Domain.Shared;
using Infrastructure.Caches.Redis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Store;

public class RedisStore(IRedisClient redisClient, ILogger<RedisStore> logger) : IDbStore
{
    public string Name => Stores.Redis;

    private IDatabase Redis => redisClient.GetDatabase();

    public Task<bool> IsAvailableAsync() => redisClient.IsHealthyAsync();

    public async Task<IEnumerable<byte[]>> GetFlagsAsync(Guid envId, long timestamp)
    {
        // get flag ids from the index sorted set
        var index = RedisKeys.FlagIndex(envId);
        var ids = await Redis.SortedSetRangeByScoreAsync(index, timestamp, exclude: Exclude.Start);

        return await GetFlagsByIdsAsync(ids.Select(id => id.ToString()), envId);
    }

    public async Task<IEnumerable<byte[]>> GetFlagsAsync(string[] ids)
    {
        return await GetFlagsByIdsAsync(ids, envId: null);
    }

    /// <summary>
    /// Reads flag values honoring the committed pointer written by the back-end gated commit path.
    /// For each id: if the committed-pointer key <c>featbit:flag-committed:{id}</c> exists, the
    /// authoritative value is the versioned snapshot <c>featbit:flag:{id}:v{pointer}</c>; otherwise
    /// it falls back to the legacy single-value key <c>featbit:flag:{id}</c> (the BestEffort path,
    /// which writes the main key + index and never writes pointers). This auto-adapts to either
    /// mode without a mode flag. Pointer GETs and value GETs are each batched via
    /// <see cref="Task.WhenAll(System.Threading.Tasks.Task[])"/> to avoid serial round-trips.
    /// Missing values (orphan index members or absent versioned snapshots) are filtered out and
    /// logged so operators can spot drift instead of crashing downstream JSON parsing.
    /// The analogous segment read path lives in <see cref="GetSegmentsByIdsAsync"/>.
    /// </summary>
    private async Task<IEnumerable<byte[]>> GetFlagsByIdsAsync(IEnumerable<string> ids, Guid? envId)
    {
        var (values, keys) = await ReadWithPointersAsync(
            ids,
            RedisKeys.FlagCommittedPointer,
            RedisKeys.FlagVersion,
            RedisKeys.Flag);

        return FilterOrphans(values, keys, envId, "flag");
    }

    public async Task<byte[]> GetSegmentAsync(string id)
    {
        var (values, _) = await ReadWithPointersAsync(
            new[] { id },
            RedisKeys.SegmentCommittedPointer,
            RedisKeys.SegmentVersion,
            RedisKeys.Segment);
        return values[0]!;
    }

    public async Task<IEnumerable<byte[]>> GetSegmentsAsync(Guid envId, long timestamp)
    {
        // get segment ids from the index sorted set
        var index = RedisKeys.SegmentIndex(envId);
        var ids = await Redis.SortedSetRangeByScoreAsync(index, timestamp, exclude: Exclude.Start);

        var (values, keys) = await ReadWithPointersAsync(
            ids.Select(id => id.ToString()),
            RedisKeys.SegmentCommittedPointer,
            RedisKeys.SegmentVersion,
            RedisKeys.Segment);

        // for shared segments, replace empty envId with actual envId
        const string emptyEnvId = "\"envId\":\"\",";

        var orphans = new List<string>();
        var jsonBytes = new List<byte[]>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
            {
                orphans.Add(keys[i]);
                continue;
            }

            var strValue = Encoding.UTF8.GetString(value);
            if (strValue.Contains(emptyEnvId))
            {
                var newStrValue = strValue.Replace(emptyEnvId, $"\"envId\":\"{envId}\",");
                jsonBytes.Add(Encoding.UTF8.GetBytes(newStrValue));
            }
            else
            {
                jsonBytes.Add(value);
            }
        }

        LogOrphans(orphans, values.Length, envId, "segment");

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

    // Resolves each id to its authoritative value key (versioned snapshot if a committed pointer
    // exists, otherwise the legacy main key), then batch-reads the values. Returns nullable byte
    // arrays (null == orphan/missing key) paired with the resolved key strings so callers can
    // filter and log.
    private async Task<(byte[]?[] values, string[] keys)> ReadWithPointersAsync(
        IEnumerable<string> ids,
        Func<string, RedisKey> committedPointerKey,
        Func<string, long, RedisKey> versionKey,
        Func<string, RedisKey> mainKey)
    {
        var idList = ids.ToList();

        // batch the committed-pointer GETs
        var pointerTasks = idList.Select(id => Redis.StringGetAsync(committedPointerKey(id)));
        var pointers = await Task.WhenAll(pointerTasks);

        // resolve each id to its authoritative value key
        var resolvedKeys = new RedisKey[idList.Count];
        for (var i = 0; i < idList.Count; i++)
        {
            var pointer = pointers[i];
            resolvedKeys[i] = pointer.HasValue
                ? versionKey(idList[i], (long)pointer)
                : mainKey(idList[i]);
        }

        // batch the value GETs
        var valueTasks = new Task<RedisValue>[resolvedKeys.Length];
        for (var i = 0; i < resolvedKeys.Length; i++)
        {
            valueTasks[i] = Redis.StringGetAsync(resolvedKeys[i]);
        }

        var rawValues = await Task.WhenAll(valueTasks);

        var values = new byte[]?[rawValues.Length];
        var keyStrings = new string[resolvedKeys.Length];
        for (var i = 0; i < rawValues.Length; i++)
        {
            values[i] = rawValues[i].HasValue ? (byte[])rawValues[i]! : null;
            keyStrings[i] = resolvedKeys[i].ToString();
        }

        return (values, keyStrings);
    }

    // Filters out entries whose backing key was missing (null) and logs the orphan keys so
    // operators can spot accumulating drift between an env's index and its values. Without this
    // filter, a single orphan index member produces a null byte[] that crashes JsonDocument.Parse
    // downstream and aborts the entire env's data-sync.
    private IEnumerable<byte[]> FilterOrphans(
        byte[]?[] values,
        string[] keys,
        Guid? envId,
        string entityName)
    {
        var orphans = new List<string>();
        var jsonBytes = new List<byte[]>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is not null)
            {
                jsonBytes.Add(value);
            }
            else
            {
                orphans.Add(keys[i]);
            }
        }

        LogOrphans(orphans, values.Length, envId, entityName);

        return jsonBytes;
    }

    private void LogOrphans(List<string> orphans, int totalCount, Guid? envId, string entityName)
    {
        if (orphans.Count == 0)
        {
            return;
        }

        if (envId.HasValue)
        {
            logger.LogWarning(
                "Orphan {EntityName} index members in env {EnvId}: {OrphanCount} of {TotalCount}. Missing keys: {MissingKeys}",
                entityName, envId.Value, orphans.Count, totalCount, string.Join(", ", orphans)
            );
        }
        else
        {
            logger.LogWarning(
                "Orphan {EntityName} ids requested: {OrphanCount} of {TotalCount}. Missing keys: {MissingKeys}",
                entityName, orphans.Count, totalCount, string.Join(", ", orphans)
            );
        }
    }
}
