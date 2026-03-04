namespace Api.Configuration;

public static class ConfigurationExtensions
{
    public static bool UseControlPlane(this IConfiguration configuration)
    {
        return configuration.GetValue<bool>("UseControlPlane");
    }

    public static int GetHeartbeatIntervalSeconds(this IConfiguration configuration)
    {
        return configuration.GetValue<int>("HeartbeatIntervalSeconds");
    }
}