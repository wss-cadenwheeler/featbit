using Api.Configuration;
using Api.Cors;
using Api.Health;
using Api.RateLimiting;
using Api.Services;
using Domain.Workspaces;
using Infrastructure;
using Infrastructure.Services;
using Serilog;
using Streaming;
using Streaming.DependencyInjection;

namespace Api.Setup;

public static class ServicesRegister
{
    public static WebApplicationBuilder RegisterServices(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.AddControllers();

        // serilog
        builder.Services.AddSerilog((_, lc) => ConfigureSerilog.Configure(lc, builder.Configuration));

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // health check dependencies
        var healthChecks = services.AddHealthChecks().AddReadinessChecks(configuration);

        // cors
        builder.AddCustomCors();

        // add bounded memory cache
        services.AddSingleton<BoundedMemoryCache>();

        // streaming services
        services
            .AddStreamingCore(options => configuration.GetSection(StreamingOptions.Streaming).Bind(options))
            .UseStore(configuration)
            .UseMq(configuration);

        // rate limiting
        if (configuration.IsRateLimitingEnabled())
        {
            builder.AddRateLimiting();
        }

        // application services
        LicenseVerifier.ImportPublicKey(configuration["PublicKey"]);
        services.AddTransient<IRelayProxyAppService, RelayProxyAppService>();
        services.AddTransient<IFeatureFlagService, FeatureFlagService>();

        if (configuration.UseControlPlane())
        {
            // D5 (#22): shared singleton recording the last successful heartbeat publish, plus the
            // freshness health check that surfaces a Degraded (not Unhealthy) self-fence signal under
            // GatedCommit. Tagged Readiness so it appears on /health/readiness; ASP.NET Core maps
            // Degraded -> HTTP 200 by default (only Unhealthy -> 503), so a Degraded result is
            // observational and does NOT fail readiness.
            services.AddSingleton<IHeartbeatPublishStatus, HeartbeatPublishStatus>();
            healthChecks.AddCheck<HeartbeatFreshnessHealthCheck>(
                "heartbeat-freshness",
                tags: new[] { HealthCheckBuilderExtensions.ReadinessTag });

            services.AddHostedService<HeartbeatService>();
        }

        return builder;
    }
}