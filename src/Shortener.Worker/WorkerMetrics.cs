using System.Diagnostics.Metrics;
using Shortener.Shared;

namespace Shortener.Worker;

/// <summary>The <c>shortener.worker.*</c> instruments from docs/contract.md (names are a dashboard contract).</summary>
internal sealed class WorkerMetrics
{
    // Defaults are tuned for milliseconds, which is useless for seconds-based histograms.
    private static readonly double[] DurationBuckets =
        [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];
    // Lag includes the API's buffer, the queue and up to BatchMaxWaitMs here, so resolve 0.1-3 s well.
    private static readonly double[] LagBuckets =
        [0.01, 0.05, 0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 5, 10, 30, 60, 120, 300, 600];
    private static readonly int[] BatchSizeBuckets = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000];

    private readonly Counter<long> _clicksProcessed;
    private readonly Histogram<int> _batchSize;
    private readonly Histogram<double> _batchDuration;
    private readonly Histogram<double> _clickLag;
    private readonly Counter<long> _batchFailures;
    private readonly Counter<long> _deadLettered;

    public WorkerMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(DiagnosticNames.Worker);

        _clicksProcessed = meter.CreateCounter<long>(
            "shortener.worker.clicks.processed", description: "Clicks stored in Postgres.");
        _batchSize = meter.CreateHistogram(
            "shortener.worker.batch.size", unit: null, description: "Clicks per stored batch.", tags: null,
            advice: new InstrumentAdvice<int> { HistogramBucketBoundaries = BatchSizeBuckets });
        _batchDuration = meter.CreateHistogram(
            "shortener.worker.batch.duration", unit: "s", description: "Time to store and ack one batch.",
            tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
        _clickLag = meter.CreateHistogram(
            "shortener.worker.click.lag", unit: "s", description: "Time from redirect to the click being stored.",
            tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LagBuckets });
        _batchFailures = meter.CreateCounter<long>(
            "shortener.worker.batch.failures", description: "Batches given up on after retries and requeued.");
        _deadLettered = meter.CreateCounter<long>(
            "shortener.worker.dead_lettered", description: "Malformed messages sent to the dead-letter queue.");
    }

    /// <summary>Call while the batch activity is current, so the histograms get trace exemplars.</summary>
    public void BatchStored(List<ClickDelivery> batch, TimeSpan duration)
    {
        _clicksProcessed.Add(batch.Count);
        _batchSize.Record(batch.Count);
        _batchDuration.Record(duration.TotalSeconds);

        var now = DateTimeOffset.UtcNow;
        foreach (var delivery in batch)
        {
            // Clamp: container clocks can disagree by a few ms.
            _clickLag.Record(Math.Max(0, (now - delivery.Click.At).TotalSeconds));
        }
    }

    public void BatchFailed() => _batchFailures.Add(1);

    public void DeadLettered() => _deadLettered.Add(1);
}
