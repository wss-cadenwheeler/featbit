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
                    return new RedisClient(connStr);
                })
                .ToList();

            services.AddSingleton<IRedisClient>(clients[0]);

            // Pair each cache service with its instance's DcId so broadcast results
            // can be keyed by DC. The first instance remains the local DC by
            // convention. An empty DcId falls back to the ordinal index so a
            // misconfiguration does not crash startup, but it is logged as a warning.
            var dcCacheServices = clients
                .Select((client, index) =>
                {
                    var dcId = redisInstances[index].DcId;
                    if (string.IsNullOrWhiteSpace(dcId))
                    {
                        dcId = index.ToString();
                    }

                    return new DcCacheService(dcId, new RedisCacheService(client));
                })
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