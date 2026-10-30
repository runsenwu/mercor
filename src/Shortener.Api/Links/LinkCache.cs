using System.Diagnostics;
using Microsoft.Extensions.Options;
using Shortener.Api.Telemetry;
using StackExchange.Redis;

namespace Shortener.Api.Links;

internal enum LinkState { NotFound, Gone, Found }

internal readonly record struct Resolution(LinkState State, string? Url);

/// <summary>Redis cache-aside for redirects: link:{code} -> long URL, or the sentinels ~404 / ~410.</summary>
internal sealed class LinkCache(IConnectionMultiplexer redis, IOptions<CacheOptions> options, ApiMetrics metrics,
    ILogger<LinkCache> logger)
{
    private const string NotFound = "~404", Gone = "~410";
    private readonly CacheOptions _options = options.Value;
    private long _nextWarning;

    public bool Enabled => _options.Enabled;
    public TimeSpan MaxTtl => TimeSpan.FromSeconds(_options.TtlSeconds);
    private TimeSpan NegativeTtl => TimeSpan.FromSeconds(_options.NegativeTtlSeconds);

    private static RedisKey Key(string code) => "link:" + code;

    /// <summary>Null = miss or Redis unavailable (caller falls back to Postgres).</summary>
    public async ValueTask<(Resolution? Hit, bool Available)> GetAsync(string code)
    {
        var start = Stopwatch.GetTimestamp();
        string? value;
        try
        {
            value = await redis.GetDatabase().StringGetAsync(Key(code));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            Warn(ex);
            return (null, false);
        }
        metrics.CacheLookup(Stopwatch.GetElapsedTime(start));

        switch (value)
        {
            case null: metrics.CacheRequest("miss"); return (null, true);
            case NotFound: metrics.CacheRequest("negative_hit"); return (new Resolution(LinkState.NotFound, null), true);
            case Gone: metrics.CacheRequest("negative_hit"); return (new Resolution(LinkState.Gone, null), true);
            default: metrics.CacheRequest("hit"); return (new Resolution(LinkState.Found, value), true);
        }
    }

    /// <summary>
    /// Read-path fill. SET NX + fire-and-forget: never blocks the redirect, and never overwrites a value
    /// written concurrently by create/delete (which would resurrect a deleted link for a whole TTL).
    /// </summary>
    public void Fill(string code, Resolution resolution, TimeSpan? positiveTtl)
    {
        var (value, ttl) = resolution.State switch
        {
            LinkState.Found => (resolution.Url!, positiveTtl),
            LinkState.Gone => (Gone, NegativeTtl),
            _ => (NotFound, (TimeSpan?)NegativeTtl),
        };
        if (ttl is null) return;
        try
        {
            redis.GetDatabase().StringSet(Key(code), value, ttl, When.NotExists, CommandFlags.FireAndForget);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            Warn(ex);
        }
    }

    /// <summary>Write path (create/delete): unconditional, awaited, so the change is visible when we return.</summary>
    public async Task SetAsync(string code, string? url, TimeSpan? ttl)
    {
        if (!Enabled) return;
        try
        {
            await redis.GetDatabase().StringSetAsync(Key(code), url ?? Gone, url is null ? NegativeTtl : ttl, When.Always);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            Warn(ex);
        }
    }

    // At most one warning per 10 s, so a Redis outage doesn't flood the logs at full request rate.
    private void Warn(Exception ex)
    {
        var now = Environment.TickCount64;
        var next = Interlocked.Read(ref _nextWarning);
        if (now >= next && Interlocked.CompareExchange(ref _nextWarning, now + 10_000, next) == next)
            logger.LogWarning(ex, "Redis unavailable; serving redirects from Postgres");
    }
}
