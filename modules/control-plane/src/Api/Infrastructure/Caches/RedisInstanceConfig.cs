namespace Api.Infrastructure.Caches;

public class RedisInstanceConfig
{
    public string ConnectionString { get; set; } = "";
    
    public string? Password { get; set; }
}