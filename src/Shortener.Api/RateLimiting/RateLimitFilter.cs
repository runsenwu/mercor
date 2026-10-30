using System.Globalization;
using Microsoft.Extensions.Options;
using Shortener.Api.Auth;
using Shortener.Api.Telemetry;
using StackExchange.Redis;

namespace Shortener.Api.RateLimiting;

/// <summary>
/// Fixed one-minute windows counted in Redis (rl:{bucket}:{partition}:{unixMinute}), so limits hold across replicas.
/// GETs use the "read" bucket; POST/DELETE use the "create" (write) bucket.
/// </summary>
internal sealed class RateLimitFilter(IConnectionMultiplexer redis, IOptions<RateLimitOptions> options, ApiMetrics metrics,
    TimeProvider time, ILogger<RateLimitFilter> logger) : IEndpointFilter
{
    // INCR + EXPIRE atomically in one round trip, so a key can never be left without a TTL.
    private const string Script = """
        local c = redis.call('INCR', KEYS[1])
        if c == 1 then redis.call('EXPIRE', KEYS[1], ARGV[1]) end
        return c
        """;
    private static readonly RedisValue[] ExpireArgs = [70];

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var caller = Caller.Resolve(http);
        var bucket = HttpMethods.IsGet(http.Request.Method) ? "read" : "create";
        var (partition, limit) = caller.IsAnonymous
            ? (ClientIp(http), bucket == "read" ? options.Value.AnonymousReadsPerMinute : options.Value.AnonymousCreatesPerMinute)
            : (caller.KeyName!, caller.PermitsPerMinute);

        var now = time.GetUtcNow().ToUnixTimeSeconds();
        long count;
        try
        {
            count = (long)await redis.GetDatabase().ScriptEvaluateAsync(Script, [$"rl:{bucket}:{partition}:{now / 60}"], ExpireArgs);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Fail open: a Redis outage should degrade limiting, not take the whole API down with it.
            logger.LogWarning(ex, "Rate limiter unavailable; allowing request");
            return await next(context);
        }

        if (count <= limit) return await next(context);

        var retryAfter = (int)(60 - now % 60);
        metrics.RateLimitRejected(bucket, caller.AuthTag);
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Problem(statusCode: 429, title: "Too many requests",
            detail: $"Limit of {limit} {bucket} requests per minute exceeded. Retry in {retryAfter} s.");
    }

    private static string ClientIp(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return "unknown";
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}
