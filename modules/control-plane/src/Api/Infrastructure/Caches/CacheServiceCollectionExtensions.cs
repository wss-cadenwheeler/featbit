using Application.Caches;
using Infrastructure;
using Infrastructure.Caches;
using Infrastructure.Caches.None;
using Infrastructure.Caches.Redis;

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
                .Get<string[]>() ?? [];
            
            if (redisInstances is { Length: > 0 })
            {
                var clients = redisInstances
                    .Select(connStr => new RedisClient(connStr))
                    .ToList();

                services.AddSingleton<IRedisClient>(clients[0]);

                var cacheServices = clients
                    .Select(client => (ICacheService)new RedisCacheService(client))
                    .ToList();
                
                services.AddTransient<ICacheService, RedisCacheService>();
                
                services.AddKeyedSingleton<ICacheService>("compositeCache", (_, _) => new CompositeRedisCacheService(cacheServices));
            }
            else
            {
                services.TryAddRedis(configuration);
                services.AddTransient<ICacheService, RedisCacheService>();
            }
        }
    }
}