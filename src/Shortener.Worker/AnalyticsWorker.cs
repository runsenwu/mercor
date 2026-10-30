using System.Threading.Channels;

namespace Shortener.Worker;

/// <summary>
/// Wires the RabbitMQ consumer to the batch loop and orders graceful shutdown:
/// stop consuming, flush and ack the in-flight batch, then close the connection.
/// </summary>
internal sealed class AnalyticsWorker(
    ClickConsumer consumer, ClickBatchProcessor processor, ILogger<AnalyticsWorker> logger) : BackgroundService
{
    // Fires only if graceful shutdown overruns HostOptions.ShutdownTimeout; aborts the final flush.
    private readonly CancellationTokenSource _abort = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Unbounded is safe: the broker never hands us more than Prefetch unacked messages, and every buffered
        // click is unacked, so Prefetch is the real bound (and our backpressure).
        var buffer = Channel.CreateUnbounded<ClickDelivery>(new UnboundedChannelOptions { SingleReader = true });

        // Deliberately not tied to stoppingToken: on shutdown it must drain and ack what is already buffered.
        var batching = processor.RunAsync(buffer.Reader, _abort.Token);
        try
        {
            // Returns on shutdown, after the consumer has been cancelled.
            await consumer.RunAsync(buffer.Writer, stoppingToken);
        }
        finally
        {
            buffer.Writer.TryComplete();
            await batching;
            await consumer.CloseAsync();
            logger.LogInformation("Analytics worker stopped");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await using var _ = cancellationToken.Register(_abort.Cancel);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _abort.Dispose();
        base.Dispose();
    }
}
