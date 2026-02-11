using Microsoft.Extensions.Configuration;

namespace Application.Configuration;

public static class ConfigurationExtensions
{
    public static bool UseControlPlane(this IConfiguration configuration)
    {
        return configuration.GetValue<bool>("UseControlPlane");
    }
}