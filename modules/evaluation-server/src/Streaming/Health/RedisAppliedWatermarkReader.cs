using Infrastructure.Caches.Redis;
using StackExchange.Redis;
using Streaming.Connections;

namespace Streaming.Health;

/// <summary>
/// <see cref="IAppliedWatermarkReader"/> that derives the per-env applied watermark from the
/// local DC Redis flag index. For each env it reads the single highest-scored member of
/// <c>featbit:flag-index:{envId}</c> (<c>ZREVRANGEBYSCORE ... LIMIT 0 1 WITHSCORES</c>) and
/// reports that score — the latest committed flag version this DC's Redis holds. The value is
/// identical for every pod in the DC and is correct immediately on a fresh pod, because it is
/// read from shared serving state rather than per-pod in-memory progress.
/// </summary>
public sealed class RedisAppliedWatermarkReader(
    IRedisClient redisClient,
    IConnectionManager connectionManager) : IAppliedWatermarkReader
{
    public async Task<Dictionary<Guid, long>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var db = redisClient.GetDatabase();

        var envIds = EnumerateEnvIds();

        var result = new Dictionary<Guid, long>();
        foreach (var envId in envIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var watermark = await ReadEnvWatermarkAsync(db, envId);
            if (watermark.HasValue)
            {
                result[envId] = watermark.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the max committed score in the env's flag index, or <c>null</c> when the env has no
    /// committed flags. Uses a descending range limited to one member with scores, which maps to
    /// <c>ZREVRANGEBYSCORE key +inf -inf WITHSCORES LIMIT 0 1</c>.
    /// </summary>
    private static async Task<long?> ReadEnvWatermarkAsync(IDatabase db, Guid envId)
    {
        var entries = await db.SortedSetRangeByScoreWithScoresAsync(
            RedisKeys.FlagIndex(envId),
            order: Order.Descending,
            take: 1);

        if (entries.Length == 0)
        {
            return null;
        }

        // Scores are stored as the committed unix-ms timestamp; round to the nearest long.
        return (long)entries[0].Score;
    }

    /// <summary>
    /// Determines the set of envs to report. Primary source is the pod's active connections
    /// (cheap, in-memory). If the pod currently has no connections, falls back to scanning the
    /// Redis flag-index keyspace so a freshly started pod still reports the DC's serving state.
    /// </summary>
    private IReadOnlyCollection<Guid> EnumerateEnvIds()
    {
        var fromConnections = connectionManager.GetAllConnections()
            .Select(c => c.EnvId)
            .ToHashSet();

        if (fromConnections.Count > 0)
        {
            return fromConnections;
        }

        return ScanFlagIndexEnvIds();
    }

    /// <summary>
    /// Scans <c>featbit:flag-index:*</c> on every (non-replica) server and parses the env id out
    /// of each key. SCAN is cursor-based and does not block Redis like KEYS.
    /// </summary>
    private HashSet<Guid> ScanFlagIndexEnvIds()
    {
        var envIds = new HashSet<Guid>();
        var connection = redisClient.Connection;

        foreach (var endpoint in connection.GetEndPoints())
        {
            var server = connection.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            foreach (var key in server.Keys(pattern: RedisKeys.FlagIndexScanPattern))
            {
                var envId = RedisKeys.TryParseFlagIndexEnvId(key.ToString());
                if (envId.HasValue)
                {
                    envIds.Add(envId.Value);
                }
            }
        }

        return envIds;
    }
}
