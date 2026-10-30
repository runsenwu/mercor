using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Shortener.Shared;

namespace Shortener.Worker;

/// <summary>
/// The single batch loop: drains the in-process buffer into batches, writes each batch in one transaction,
/// then acks it with one multiple-ack.
/// </summary>
/// <remarks>
/// Delivery is at-least-once. If the process crashes (or the channel dies) after the commit but before the ack,
/// the broker redelivers the batch and those clicks are counted twice. That is acceptable for analytics
/// counters. If it weren't, the API would stamp each message with an id and this transaction would insert the
/// ids into a processed-messages table (unique key) and skip duplicates: the idempotent-consumer/inbox pattern.
/// </remarks>
internal sealed class ClickBatchProcessor(
    ClickStore store, WorkerMetrics metrics, IOptions<WorkerOptions> options, ILogger<ClickBatchProcessor> logger)
{
    // Four attempts in ~4 s, then the batch is requeued (possibly to another replica).
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    // Keeps batch spans small; enough to jump from a batch to sample redirects.
    private const int MaxSpanLinks = 100;

    private readonly int _batchSize = options.Value.BatchSize;
    private readonly TimeSpan _maxWait = options.Value.BatchMaxWait;

    /// <summary>
    /// Runs until the buffer is completed and drained, so on shutdown the last batch is still stored and acked.
    /// <paramref name="abortToken"/> fires only if that takes longer than the host's shutdown timeout.
    /// </summary>
    public async Task RunAsync(ChannelReader<ClickDelivery> buffer, CancellationToken abortToken)
    {
        logger.LogInformation("Batching up to {BatchSize} clicks or {MaxWaitMs} ms",
            _batchSize, _maxWait.TotalMilliseconds);
        var batch = new List<ClickDelivery>(_batchSize);
        try
        {
            while (await buffer.WaitToReadAsync(abortToken))
            {
                await FillAsync(buffer, batch, abortToken);
                await FlushAsync(batch, abortToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (abortToken.IsCancellationRequested)
        {
            logger.LogWarning("Shutdown timed out with {Count} clicks unacked; the broker will redeliver them",
                batch.Count);
        }
    }

    // Returns when the batch is full, its oldest click has waited _maxWait, or the buffer was completed.
    private async Task FillAsync(ChannelReader<ClickDelivery> buffer, List<ClickDelivery> batch, CancellationToken abortToken)
    {
        while (true)
        {
            while (batch.Count < _batchSize && buffer.TryRead(out var delivery)) batch.Add(delivery);
            if (batch.Count == 0 || batch.Count >= _batchSize) return;

            var remaining = _maxWait - Stopwatch.GetElapsedTime(batch[0].ReceivedTimestamp);
            if (remaining <= TimeSpan.Zero) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(abortToken);
            timeout.CancelAfter(remaining);
            try
            {
                // false = completed, i.e. we are shutting down: flush what we have now.
                if (!await buffer.WaitToReadAsync(timeout.Token)) return;
            }
            catch (OperationCanceledException) when (!abortToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task FlushAsync(List<ClickDelivery> batch, CancellationToken abortToken)
    {
        // A closed channel means the broker has already requeued these and will redeliver them;
        // storing them as well would count them twice.
        var stale = batch.RemoveAll(d => !d.Channel.IsOpen);
        if (stale > 0)
        {
            logger.LogWarning("Dropped {Count} buffered clicks from a closed channel; the broker redelivers them", stale);
        }
        if (batch.Count == 0) return;

        var started = Stopwatch.GetTimestamp();
        using var activity = StartBatchActivity(batch);
        var aggregate = ClickAggregator.Aggregate(batch.Select(d => d.Click));
        activity?.SetTag("shortener.batch.codes", aggregate.PerCode.Length);

        if (await TryWriteAsync(aggregate, batch.Count, activity, abortToken))
        {
            await SettleAsync(batch, ack: true);
            metrics.BatchStored(batch, Stopwatch.GetElapsedTime(started));
        }
        else
        {
            metrics.BatchFailed();
            await SettleAsync(batch, ack: false);
        }
    }

    private async Task<bool> TryWriteAsync(ClickAggregate aggregate, int clicks, Activity? activity, CancellationToken abortToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.WriteAsync(aggregate, abortToken);
                return true;
            }
            catch (Exception ex) when (!abortToken.IsCancellationRequested)
            {
                if (attempt > RetryDelays.Length)
                {
                    logger.LogError(ex, "Giving up on a batch of {Clicks} clicks after {Attempts} attempts; requeueing it",
                        clicks, attempt);
                    activity?.AddException(ex);
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    return false;
                }

                var delay = RetryDelays[attempt - 1];
                logger.LogWarning(ex, "Storing a batch of {Clicks} clicks failed (attempt {Attempt}); retrying in {Delay}",
                    clicks, attempt, delay);
                await Task.Delay(delay, abortToken);
            }
        }
    }

    // Acks (or requeues) the whole batch with one multiple-ack per channel. That covers exactly this batch because
    // tags only grow on a channel, the buffer preserves delivery order, and every earlier delivery was settled by
    // an earlier batch or individually dead-lettered before this batch's deliveries were buffered.
    private async Task SettleAsync(List<ClickDelivery> batch, bool ack)
    {
        foreach (var group in batch.GroupBy(d => d.Channel))
        {
            var lastTag = group.Max(d => d.DeliveryTag);
            try
            {
                if (ack)
                    await group.Key.BasicAckAsync(lastTag, multiple: true);
                else
                    await group.Key.BasicNackAsync(lastTag, multiple: true, requeue: true);
            }
            catch (Exception ex)
            {
                // The channel died in between: the broker redelivers these, so after a commit they count twice.
                logger.LogWarning(ex, "Could not {Action} {Count} clicks; the broker will redeliver them",
                    ack ? "ack" : "requeue", group.Count());
            }
        }
    }

    private static Activity? StartBatchActivity(List<ClickDelivery> batch)
    {
        if (!WorkerTelemetry.Source.HasListeners()) return null;

        // A batch has many parents, so rather than picking one it starts its own trace and links to the redirect
        // traces of its clicks. Span links are the OpenTelemetry pattern for batch processing.
        var links = new List<ActivityLink>(Math.Min(batch.Count, MaxSpanLinks));
        foreach (var delivery in batch)
        {
            if (delivery.TraceContext == default) continue;
            links.Add(new ActivityLink(delivery.TraceContext));
            if (links.Count == MaxSpanLinks) break;
        }

        return WorkerTelemetry.Source.StartActivity(
            "clicks.batch",
            ActivityKind.Consumer,
            parentContext: default,
            tags:
            [
                new("messaging.system", "rabbitmq"),
                new("messaging.operation.type", "process"),
                new("messaging.destination.name", ClickTopology.Exchange),
                new("messaging.consumer.group.name", ClickTopology.Queue),
                new("messaging.batch.message_count", batch.Count),
            ],
            links: links);
    }
}
