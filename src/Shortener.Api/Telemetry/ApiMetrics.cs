using System.Diagnostics;
using System.Diagnostics.Metrics;
using Shortener.Shared;

namespace Shortener.Api.Telemetry;

internal static class ApiDiagnostics
{
    public static readonly ActivitySource Source = new(DiagnosticNames.Api);
}

/// <summary>Custom metrics (Meter "Shortener.Api"); names and tag values are part of docs/contract.md.</summary>
internal sealed class ApiMetrics
{
    // Cache lookups are sub-millisecond; default SDK buckets would lump them all together.
    private static readonly double[] LookupBuckets = [0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1];

    private readonly Meter _meter;
    private readonly Counter<long> _cacheRequests, _redirects, _linksCreated, _collisions, _rateLimited,
        _enqueued, _dropped, _published, _publishFailures;
    private readonly Histogram<double> _cacheLookup;

    public ApiMetrics(IMeterFactory factory)
    {
        _meter = factory.Create(DiagnosticNames.Api);
        _cacheRequests = _meter.CreateCounter<long>("shortener.cache.requests");
        _cacheLookup = _meter.CreateHistogram<double>("shortener.cache.lookup.duration", "s", null, null,
            new InstrumentAdvice<double> { HistogramBucketBoundaries = LookupBuckets });
        _redirects = _meter.CreateCounter<long>("shortener.redirects");
        _linksCreated = _meter.CreateCounter<long>("shortener.links.created");
        _collisions = _meter.CreateCounter<long>("shortener.code.collisions");
        _rateLimited = _meter.CreateCounter<long>("shortener.ratelimit.rejected");
        _enqueued = _meter.CreateCounter<long>("shortener.clicks.enqueued");
        _dropped = _meter.CreateCounter<long>("shortener.clicks.dropped");
        _published = _meter.CreateCounter<long>("shortener.clicks.published");
        _publishFailures = _meter.CreateCounter<long>("shortener.clicks.publish_failures");
    }

    public void CacheRequest(string result) => _cacheRequests.Add(1, new KeyValuePair<string, object?>("result", result));
    public void CacheLookup(TimeSpan elapsed) => _cacheLookup.Record(elapsed.TotalSeconds);
    public void Redirect(string result) => _redirects.Add(1, new KeyValuePair<string, object?>("result", result));
    public void LinkCreated(string auth) => _linksCreated.Add(1, new KeyValuePair<string, object?>("auth", auth));
    public void CodeCollision() => _collisions.Add(1);
    public void RateLimitRejected(string bucket, string auth) => _rateLimited.Add(1,
        new KeyValuePair<string, object?>("bucket", bucket), new KeyValuePair<string, object?>("auth", auth));
    public void ClickEnqueued() => _enqueued.Add(1);
    public void ClickDropped() => _dropped.Add(1);
    public void ClickPublished() => _published.Add(1);
    public void ClickPublishFailed() => _publishFailures.Add(1);
    public void ObserveClickBuffer(Func<int> size) => _meter.CreateObservableGauge("shortener.clicks.buffer.size", size);
}
