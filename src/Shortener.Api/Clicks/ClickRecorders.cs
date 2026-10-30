using System.Diagnostics;
using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Shortener.Api.Telemetry;

namespace Shortener.Api.Clicks;

internal interface IClickRecorder
{
    ValueTask RecordAsync(string code);
}

internal readonly record struct PendingClick(string Code, DateTimeOffset At, ActivityContext Parent);

/// <summary>queue mode: a bounded in-memory buffer drained by <see cref="ClickPublisher"/>.</summary>
internal sealed class ClickBuffer : IClickRecorder
{
    private readonly Channel<PendingClick> _channel;
    private readonly ApiMetrics _metrics;
    private readonly TimeProvider _time;

    public ClickBuffer(IOptions<ClickOptions> options, ApiMetrics metrics, TimeProvider time)
    {
        // FullMode.Wait + TryWrite: TryWrite returns false when full, so drops are counted.
        // (DropWrite would report success and hide them.)
        _channel = Channel.CreateBounded<PendingClick>(new BoundedChannelOptions(options.Value.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false,
        });
        _metrics = metrics;
        _time = time;
        metrics.ObserveClickBuffer(() => _channel.Reader.Count);
    }

    public ChannelReader<PendingClick> Reader => _channel.Reader;

    public ValueTask RecordAsync(string code)
    {
        if (_channel.Writer.TryWrite(new PendingClick(code, _time.GetUtcNow(), Activity.Current?.Context ?? default)))
            _metrics.ClickEnqueued();
        else
            _metrics.ClickDropped();
        return ValueTask.CompletedTask;
    }
}

/// <summary>sync mode: update counters inside the request, to demonstrate hot-row contention.</summary>
internal sealed class SyncClickRecorder(NpgsqlDataSource db, ILogger<SyncClickRecorder> logger) : IClickRecorder
{
    public async ValueTask RecordAsync(string code)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.ExecuteAsync("""
                UPDATE links SET click_count = click_count + 1 WHERE code = @code;
                INSERT INTO link_clicks_daily (code, day, clicks) VALUES (@code, (now() AT TIME ZONE 'UTC')::date, 1)
                ON CONFLICT (code, day) DO UPDATE SET clicks = link_clicks_daily.clicks + 1;
                """, new { code });
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // Analytics never fails a redirect.
            logger.LogWarning(ex, "Failed to record click synchronously");
        }
    }
}

internal sealed class NoopClickRecorder : IClickRecorder
{
    public ValueTask RecordAsync(string code) => ValueTask.CompletedTask;
}
