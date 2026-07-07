using Infrastructure.Caches.Redis;
using StackExchange.Redis;
using Streaming.Connections;

namespace Streaming.Health;

/// <summary>
/// <see cref="IAppliedWatermarkReader"/> that derives the per-env applied watermark from the
/// local DC Redis flag and segment indexes. For each env it reads the single highest-scored
/// member of both <c>featbit:flag-index:{envId}</c> and <c>featbit:segment-index:{envId}</c>
/// (<c>ZREVRANGEBYSCORE ... LIMIT 0 1 WITHSCORES</c>) and reports the max of the two scores —
/// the latest committed flag or segment version this DC's Redis holds. The value is identical
/// for every pod in the DC and is correct immediately on a fresh pod, because it is read from
/// shared serving state rather than per-pod in-memory progress.
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
    /// Reads the max committed score across the env's flag index and segment index, or
    /// <c>null</c> when the env has no committed flags or segments. Uses a descending range
    /// limited to one member with scores, which maps to
    /// <c>ZREVRANGEBYSCORE key +inf -inf WITHSCORES LIMIT 0 1</c> against each index; the env is
    /// reported if either index is non-empty.
    /// </summary>
    private static async Task<long?> ReadEnvWatermarkAsync(IDatabase db, Guid envId)
    {
        var flagTopScore = await ReadTopScoreAsync(db, RedisKeys.FlagIndex(envId));
        var segmentTopScore = await ReadTopScoreAsync(db, RedisKeys.SegmentIndex(envId));

        if (!flagTopScore.HasValue && !segmentTopScore.HasValue)
        {
            return null;
        }

        return Math.Max(flagTopScore ?? long.MinValue, segmentTopScore ?? long.MinValue);
    }

    /// <summary>
    /// Reads the single highest-scored member of a sorted-set index, or <c>null</c> if it is
    /// empty.
    /// </summary>
    private static async Task<long?> ReadTopScoreAsync(IDatabase db, RedisKey indexKey)
    {
        var entries = await db.SortedSetRangeByScoreWithScoresAsync(
            indexKey,
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
    /// Redis flag-index and segment-index keyspaces so a freshly started pod still reports the
    /// DC's serving state.
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

        return ScanIndexEnvIds();
    }

    /// <summary>
    /// Scans both <c>featbit:flag-index:*</c> and <c>featbit:segment-index:*</c> on every
    /// (non-replica) server and unions the env ids parsed out of the matching keys. SCAN is
    /// cursor-based and does not block Redis like KEYS.
    /// </summary>
    private HashSet<Guid> ScanIndexEnvIds()
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

            foreach (var key in server.Keys(pattern: RedisKeys.SegmentIndexScanPattern))
            {
                var envId = RedisKeys.TryParseSegmentIndexEnvId(key.ToString());
                if (envId.HasValue)
                {
                    envIds.Add(envId.Value);
                }
            }
        }

        return envIds;
    }
}
