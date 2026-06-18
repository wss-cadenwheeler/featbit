using System.Text.Json;
using Domain.FeatureFlags;
using Domain.Segments;
using Domain.Utils;
using StackExchange.Redis;

namespace Infrastructure.Caches.Redis;

public static class RedisCaches
{
    public static KeyValuePair<RedisKey, RedisValue> Flag(FeatureFlag flag)
    {
        var key = RedisKeys.Flag(flag.Id);
        var value = JsonSerializer.SerializeToUtf8Bytes(flag, ReusableJsonSerializerOptions.Web);

        return new KeyValuePair<RedisKey, RedisValue>(key, value);
    }

    public static RedisIndex FlagIndex(FeatureFlag flag)
    {
        var index = new RedisIndex
        {
            Key = RedisKeys.FlagIndex(flag.EnvId),
            Member = flag.Id.ToString(),
            Score = new DateTimeOffset(flag.UpdatedAt).ToUnixTimeMilliseconds()
        };

        return index;
    }

    // --- B1 stage/commit storage -------------------------------------------------------------
    // To keep the old committed flag value readable while a new version is staged, a staged value
    // is written under a versioned key derived from the existing flag value key (RedisKeys.Flag):
    //   featbit:flag:{id}:v{ts}        -- immutable per-version snapshot ("staged" value)
    // A separate committed-pointer key records which version is currently authoritative:
    //   featbit:flag-committed:{id}    -- value is the committed timestamp ({ts}) as a string
    // Staging writes only the versioned key; committing flips the pointer (and the env index).
    // Both keys reuse the existing FlagPrefix convention so they share key-space ownership with
    // the legacy single-value key produced by RedisKeys.Flag / RedisCaches.Flag.

    /// <summary>
    /// The versioned, immutable value key for a single staged flag version: <c>featbit:flag:{id}:v{ts}</c>.
    /// </summary>
    public static RedisKey FlagVersion(Guid id, long ts) => new($"{RedisKeys.Flag(id)}:v{ts}");

    /// <summary>
    /// The committed-pointer key holding the timestamp of the currently committed flag version:
    /// <c>featbit:flag-committed:{id}</c>.
    /// </summary>
    public static RedisKey FlagCommittedPointer(Guid id) => new($"{FlagCommittedPrefix}{id}");

    /// <summary>
    /// Builds the versioned staged value entry for <paramref name="flag"/> scored by
    /// <paramref name="ts"/>. Mirrors <see cref="Flag(FeatureFlag)"/> serialization.
    /// </summary>
    public static KeyValuePair<RedisKey, RedisValue> FlagStaged(FeatureFlag flag, long ts)
    {
        var key = FlagVersion(flag.Id, ts);
        var value = JsonSerializer.SerializeToUtf8Bytes(flag, ReusableJsonSerializerOptions.Web);

        return new KeyValuePair<RedisKey, RedisValue>(key, value);
    }

    private const string FlagCommittedPrefix = "featbit:flag-committed:";

    public static KeyValuePair<RedisKey, RedisValue> Segment(Segment segment)
    {
        var key = RedisKeys.Segment(segment.Id);

        var json = segment.SerializeAsEnvironmentSpecific();
        var value = JsonSerializer.SerializeToUtf8Bytes(json);

        return new KeyValuePair<RedisKey, RedisValue>(key, value);
    }

    public static RedisIndex SegmentIndex(Guid envId, Segment segment)
    {
        var index = new RedisIndex
        {
            Key = RedisKeys.SegmentIndex(envId),
            Member = segment.Id.ToString(),
            Score = new DateTimeOffset(segment.UpdatedAt).ToUnixTimeMilliseconds()
        };

        return index;
    }
}