using StackExchange.Redis;

namespace Infrastructure.Caches.Redis;

public static class RedisKeys
{
    private const string FlagPrefix = "featbit:flag:";
    private const string FlagCommittedPrefix = "featbit:flag-committed:";
    private const string FlagIndexPrefix = "featbit:flag-index:";
    private const string SegmentPrefix = "featbit:segment:";
    private const string SegmentCommittedPrefix = "featbit:segment-committed:";
    private const string SegmentIndexPrefix = "featbit:segment-index:";
    private const string SecretPrefix = "featbit:secret:";
    private const string RateLimitPrefix = "featbit:rl:";

    public static RedisKey FlagIndex(Guid envId) => new($"{FlagIndexPrefix}{envId}");

    /// <summary>
    /// The glob pattern matching every per-env flag index key (<c>featbit:flag-index:*</c>),
    /// for use with Redis <c>SCAN</c> when enumerating envs that have a committed flag index.
    /// </summary>
    public static string FlagIndexScanPattern => $"{FlagIndexPrefix}*";

    /// <summary>
    /// Extracts the environment id from a flag index key (<c>featbit:flag-index:{envId}</c>),
    /// or <c>null</c> if the key does not match the expected shape.
    /// </summary>
    public static Guid? TryParseFlagIndexEnvId(string key)
        => key.StartsWith(FlagIndexPrefix, StringComparison.Ordinal)
           && Guid.TryParse(key.AsSpan(FlagIndexPrefix.Length), out var envId)
            ? envId
            : null;

    public static RedisKey Flag(string id) => new($"{FlagPrefix}{id}");

    // --- Stage/commit read keys (mirror back-end RedisCaches B1 conventions) ---------------------
    // The back-end gated commit path writes a per-version snapshot under a versioned key derived
    // from the flag value key, plus a committed-pointer key recording the authoritative version:
    //   featbit:flag:{id}:v{ts}        -- immutable per-version snapshot ("staged" value)
    //   featbit:flag-committed:{id}    -- value is the committed timestamp ({ts}) as a string
    // RedisStore reads the pointer to resolve which versioned value is authoritative; when the
    // pointer is absent it falls back to the legacy single-value Flag key (BestEffort path).

    /// <summary>
    /// The versioned, immutable value key for a single staged flag version: <c>featbit:flag:{id}:v{ts}</c>.
    /// </summary>
    public static RedisKey FlagVersion(string id, long ts) => new($"{FlagPrefix}{id}:v{ts}");

    /// <summary>
    /// The committed-pointer key holding the timestamp of the currently committed flag version:
    /// <c>featbit:flag-committed:{id}</c>.
    /// </summary>
    public static RedisKey FlagCommittedPointer(string id) => new($"{FlagCommittedPrefix}{id}");

    public static RedisKey SegmentIndex(Guid envId) => new($"{SegmentIndexPrefix}{envId}");

    public static RedisKey Segment(string id) => new($"{SegmentPrefix}{id}");

    // --- Stage/commit read keys (mirror back-end RedisCaches B2 conventions) ---------------------
    // The back-end gated segment commit path writes a per-version snapshot under a versioned key
    // derived from the segment value key, plus a committed-pointer key recording the authoritative
    // version:
    //   featbit:segment:{id}:v{ts}        -- immutable per-version snapshot ("staged" value)
    //   featbit:segment-committed:{id}    -- value is the committed timestamp ({ts}) as a string
    // RedisStore reads the pointer to resolve which versioned value is authoritative; when the
    // pointer is absent it falls back to the legacy single-value Segment key (BestEffort path).

    /// <summary>
    /// The versioned, immutable value key for a single staged segment version:
    /// <c>featbit:segment:{id}:v{ts}</c>.
    /// </summary>
    public static RedisKey SegmentVersion(string id, long ts) => new($"{SegmentPrefix}{id}:v{ts}");

    /// <summary>
    /// The committed-pointer key holding the timestamp of the currently committed segment version:
    /// <c>featbit:segment-committed:{id}</c>.
    /// </summary>
    public static RedisKey SegmentCommittedPointer(string id) => new($"{SegmentCommittedPrefix}{id}");

    public static RedisKey Secret(string secretString) => new($"{SecretPrefix}{secretString}");

    public static RedisKey RateLimit(string type, string partitionKey) => new($"{RateLimitPrefix}{type}:{partitionKey}");
}