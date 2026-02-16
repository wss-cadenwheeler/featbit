using System.Reflection;
using Api.Infrastructure.Caches;
using Api.Infrastructure.MQ;
using Api.Infrastructure.Persistence;
using Application.Bases.Behaviours;
using Application.Segments;
using Application.Services;
using Infrastructure;
using Infrastructure.AppService;
using MediatR;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddCache(builder.Configuration);
builder.Services.AddTransient<IFeatureFlagAppService, FeatureFlagAppService>();
builder.Services.AddDbSpecificServices(builder.Configuration);
builder.Services.AddMq(builder.Configuration);
builder.Services.AddTransient<ISegmentMessageService, SegmentMessageService>();

builder.Services.AddHealthChecks().AddReadinessChecks(builder.Configuration);

// MediatR
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));    

var app = builder.Build();

app.MapHealthChecks("health/liveness", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("health/readiness", new HealthCheckOptions()
{
    Predicate = registration => registration.Tags.Contains(HealthCheckBuilderExtensions.ReadinessTag)
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();


app.Run();

