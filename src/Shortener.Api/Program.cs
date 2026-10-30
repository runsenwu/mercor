using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Shortener.Api;
using Shortener.Api.Auth;
using Shortener.Api.Clicks;
using Shortener.Api.Endpoints;
using Shortener.Api.Health;
using Shortener.Api.Links;
using Shortener.Api.Telemetry;
using Shortener.Shared;
using StackExchange.Redis;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.AddShortenerTelemetry(DiagnosticNames.ApiService,
    metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("Npgsql", DiagnosticNames.Api),
    tracing => tracing
        .AddSource(DiagnosticNames.Api)
        .AddAspNetCoreInstrumentation(o => o.Filter = http => !HealthEndpoints.IsProbe(http.Request.Path))
        .AddHttpClientInstrumentation()
        .AddNpgsql()
        .AddRedisInstrumentation());

builder.Services.Configure<ShortenerOptions>(config.GetSection("Shortener"));
builder.Services.Configure<CacheOptions>(config.GetSection("Cache"));
builder.Services.Configure<ClickOptions>(config.GetSection("Clicks"));
builder.Services.Configure<RateLimitOptions>(config.GetSection("RateLimits"));

// Only nginx can reach the api containers, so trust its X-Forwarded-For for the client IP (rate-limit partition).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ApiMetrics>();

builder.Services.AddSingleton(sp => new NpgsqlDataSourceBuilder(config.GetConnectionString("Postgres"))
    .UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>())
    // Only trace commands inside a request; avoids orphan root spans.
    .ConfigureTracing(t => t.ConfigureCommandFilter(_ => Activity.Current is not null))
    .Build());
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(config.GetConnectionString("Redis") ?? "redis:6379");
    // Start even when Redis is down: redirects fall back to Postgres and rate limiting fails open.
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});

builder.Services.AddSingleton(new ApiKeyStore(config.GetSection("ApiKeys").Get<Shortener.Api.ApiKeyOptions[]>() ?? []));
builder.Services.AddSingleton<LinkRepository>();
builder.Services.AddSingleton<LinkCache>();
builder.Services.AddSingleton<LinkService>();

var clickMode = config.GetSection("Clicks").Get<ClickOptions>()?.Mode ?? ClickMode.Queue;
switch (clickMode)
{
    case ClickMode.Queue:
        builder.Services.AddSingleton<ClickBuffer>();
        builder.Services.AddSingleton<IClickRecorder>(sp => sp.GetRequiredService<ClickBuffer>());
        builder.Services.AddSingleton<ClickPublisher>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ClickPublisher>());
        break;
    case ClickMode.Sync:
        builder.Services.AddSingleton<IClickRecorder, SyncClickRecorder>();
        break;
    default:
        builder.Services.AddSingleton<IClickRecorder, NoopClickRecorder>();
        break;
}
builder.Services.AddApiHealthChecks(clickMode);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapScalarApiReference("/docs", o => o.WithTitle("Shortener API"));
app.MapHealthEndpoints();
app.MapLinkApi();
app.MapRedirects();

app.Run();
