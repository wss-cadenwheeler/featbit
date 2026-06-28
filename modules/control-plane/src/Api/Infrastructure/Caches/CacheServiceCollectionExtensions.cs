using Application.Caches;
using Infrastructure;
using Infrastructure.Caches;
using Infrastructure.Caches.None;
using Infrastructure.Caches.Redis;
using Microsoft.Extensions.Logging;

namespace Api.Infrastructure.Caches;

public static class CacheServiceCollectionExtensions
{
    public static void AddCache(this IServiceCollection services, IConfiguration configuration)
    {
        var cacheProvider = configuration.GetCacheProvider();

        switch (cacheProvider)
        {
            case CacheProvider.None:
                AddNone();
                break;
            case CacheProvider.Redis:
                AddRedis();
                break;
        }

        return;

        void AddNone()
        {
            services.AddTransient<ICacheService, NoneCacheService>();
            // No DC connections without Redis; register an empty list so the cache reconciler
            // resolves and cleanly no-ops.
            services.AddSingleton<IReadOnlyList<DcRedisConnection>>(Array.Empty<DcRedisConnection>());
        }

        void AddRedis()
        {
            var redisInstances = configuration
                .GetSection("Redis:Instances")
                .Get<RedisInstanceConfig[]>() ?? [];

            var clients = redisInstances
                .Select(instance =>
                {
                    var connStr = instance.ConnectionString;
                    if (!string.IsNullOrEmpty(instance.Password))
                        connStr += $",password={instance.Password}";
                    // Make the multiplexer self-healing. Without abortConnect=false, a peer that is
                    // unreachable at first touch makes ConnectionMultiplexer.Connect throw, and the
                    // Lazy<ConnectionMultiplexer> in RedisClient caches that exception PERMANENTLY —
                    // every later write to that DC re-throws and the cache can never self-correct.
                    // With abortConnect=false, Connect returns immediately and reconnects in the
                    // background, so a returned peer accepts the backfill that repairs its cache.
                    if (!connStr.Contains("abortConnect", StringComparison.OrdinalIgnoreCase))
                        connStr += ",abortConnect=false";
                    return new RedisClient(connStr);
                })
                .ToList();

            services.AddSingleton<IRedisClient>(clients[0]);

            // An empty DcId falls back to the ordinal index so a misconfiguration does not crash
            // startup (a warning is logged below when building the composite).
            string ResolveDcId(int index)
            {
                var dcId = redisInstances[index].DcId;
                return string.IsNullOrWhiteSpace(dcId) ? index.ToString() : dcId;
            }

            // All DC connections (index 0 is the local DC by convention; the rest are peers). The
            // cache reconciler polls each for connection state and backfills that DC from the source
            // of truth when its link becomes reachable (startup or reconnect) — the LOCAL entry is
            // what lets a cluster self-heal its own cache even when the peer is down. Reuses the SAME
            // RedisClient instances the composite writes through.
            var dcConnections = clients
                .Select((client, index) => new DcRedisConnection(ResolveDcId(index), client, index == 0))
                .ToList();
            services.AddSingleton<IReadOnlyList<DcRedisConnection>>(dcConnections);

            // Pair each cache service with its instance's DcId so broadcast results
            // can be keyed by DC. The first instance remains the local DC by convention.
            var dcCacheServices = clients
                .Select((client, index) => new DcCacheService(ResolveDcId(index), new RedisCacheService(client)))
                .ToList();

            services.AddTransient<ICacheService, RedisCacheService>();

            services.AddKeyedSingleton<ICacheService>(
                "compositeCache",
                (sp, _) =>
                {
                    var logger = sp.GetRequiredService<ILogger<CompositeRedisCacheService>>();

                    for (var i = 0; i < redisInstances.Length; i++)
                    {
                        if (string.IsNullOrWhiteSpace(redisInstances[i].DcId))
                        {
                            logger.LogWarning(
                                "Redis:Instances[{Index}] has no DcId configured; " +
                                "falling back to ordinal index '{Index}' as the DC key. " +
                                "Configure a DcId per instance for stable per-DC broadcast results.",
                                i,
                                i);
                        }
                    }

                    return new CompositeRedisCacheService(dcCacheServices, logger);
                });
        }
    }
}