using System.Reflection;
using Api.Authentication;
using Api.Infrastructure.Caches;
using Api.Infrastructure.MQ;
using Api.Infrastructure.Persistence;
using Api.Setup.OpenApi;
using Application.Bases.Behaviours;
using Application.Segments;
using Application.Services;
using Infrastructure;
using Infrastructure.AppService;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi;
using Serilog;

namespace Api.Setup;

public static class ServicesRegister
{
    public static WebApplicationBuilder RegisterServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((_, lc) => ConfigureSerilog.Configure(lc, builder.Configuration));
        builder.Services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Featbit Control Plane",
                    Version = "v1"
                };
                return Task.CompletedTask;
            });
    
            options.AddDocumentTransformer<ApiKeySecuritySchemeTransformer>();
        });
        builder.Services.AddControllers();
        builder.Services.AddCache(builder.Configuration);
        builder.Services.AddTransient<IFeatureFlagAppService, FeatureFlagAppService>();
        builder.Services.AddDbSpecificServices(builder.Configuration);
        builder.Services.AddMq(builder.Configuration);
        builder.Services.AddTransient<ISegmentMessageService, SegmentMessageService>();

        builder.Services.AddHealthChecks().AddReadinessChecks(builder.Configuration);
        
        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));    
        builder.Services.AddAuthentication("ApiKey")
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", options => { });
        
        return builder;
    }
    
    
}