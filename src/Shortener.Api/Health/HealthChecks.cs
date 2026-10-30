using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Shortener.Api.Clicks;
using StackExchange.Redis;

namespace Shortener.Api.Health;

internal sealed class PostgresHealthCheck(NpgsqlDataSource db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
        return HealthCheckResult.Healthy();
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!redis.IsConnected) return HealthCheckResult.Unhealthy("Not connected");
        await redis.GetDatabase().PingAsync();
        return HealthCheckResult.Healthy();
    }
}

internal sealed class RabbitMqHealthCheck(ClickPublisher publisher) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(publisher.IsConnected ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Publisher not connected"));
}

internal static class HealthEndpoints
{
    private const string Ready = "ready";

    public static void AddApiHealthChecks(this IServiceCollection services, ClickMode mode)
    {
        var timeout = TimeSpan.FromSeconds(3);
        var checks = services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [Ready], timeout: timeout)
            .AddCheck<RedisHealthCheck>("redis", tags: [Ready], timeout: timeout);
        // RabbitMQ is only a dependency when clicks are queued.
        if (mode == ClickMode.Queue)
            checks.AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: [Ready], timeout: timeout);
    }

    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: the process answers; no dependency checks (a DB blip must not restart every replica).
        app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false }).DisableHttpMetrics();
        app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains(Ready) }).DisableHttpMetrics();
    }

    public static bool IsProbe(PathString path) => path.StartsWithSegments("/healthz") || path.StartsWithSegments("/readyz");
}
