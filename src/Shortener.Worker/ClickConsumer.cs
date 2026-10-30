using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shortener.Shared;

namespace Shortener.Worker;

/// <summary>
/// Owns the RabbitMQ connection: connects with backoff (compose does not guarantee startup order), consumes
/// <c>clicks.analytics</c> into the in-process buffer, and starts a new session whenever the channel closes.
/// </summary>
internal sealed class ClickConsumer(
    IConfiguration configuration,
    IOptions<WorkerOptions> options,
    WorkerMetrics metrics,
    ILogger<ClickConsumer> logger)
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly ConnectionFactory _factory = new()
    {
        Uri = new Uri(configuration.GetConnectionString("RabbitMq")
            ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq is not configured.")),
        // We reconnect ourselves instead of using the client's automatic recovery: after a reconnect, buffered
        // deliveries belong to a dead channel (their tags mean nothing on the new one), and a fresh channel per
        // session makes that explicit. See ClickDelivery.Channel.
        AutomaticRecoveryEnabled = false,
        // One delivery at a time, in order (the default, but the ack logic relies on it): a dead-letter nack is
        // sent before any later delivery reaches the buffer, so a later multiple-ack never covers its tag.
        ConsumerDispatchConcurrency = 1,
        ClientProvidedName = $"{DiagnosticNames.WorkerService}@{Environment.MachineName}",
    };

    private readonly ushort _prefetch = (ushort)options.Value.Prefetch;

    private IConnection? _connection;
    private IChannel? _channel;
    private string? _consumerTag;

    /// <summary>
    /// Consumes until <paramref name="stoppingToken"/> fires. Then it cancels the consumer but leaves the channel
    /// open, so the in-flight batch can still be acked; call <see cref="CloseAsync"/> after that.
    /// </summary>
    public async Task RunAsync(ChannelWriter<ClickDelivery> buffer, CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                var closed = await StartSessionAsync(buffer, stoppingToken);
                attempt = 0;
                var reason = await closed.WaitAsync(stoppingToken);
                logger.LogWarning("RabbitMQ channel closed ({Reason}); reconnecting", reason);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await CancelConsumerAsync();
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cannot consume from RabbitMQ (attempt {Attempt})", attempt + 1);
            }

            await CloseAsync();
            var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt++), MaxBackoff.TotalSeconds));
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Closes the channel and connection; unacked messages go back to the queue.</summary>
    public async Task CloseAsync()
    {
        var (channel, connection) = (_channel, _connection);
        (_channel, _connection, _consumerTag) = (null, null, null);
        try
        {
            if (channel is not null) await channel.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error while closing the RabbitMQ connection");
        }
    }

    // Returns a task that completes (with a reason) when this session's channel closes.
    private async Task<Task<string>> StartSessionAsync(ChannelWriter<ClickDelivery> buffer, CancellationToken ct)
    {
        var connection = _connection = await _factory.CreateConnectionAsync(ct);
        var channel = _channel = await connection.CreateChannelAsync(cancellationToken: ct);

        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, args) =>
        {
            closed.TrySetResult($"{args.ReplyCode} {args.ReplyText}");
            return Task.CompletedTask;
        };

        await ClickTopology.DeclareAsync(channel, ct);
        await channel.BasicQosAsync(prefetchSize: 0, _prefetch, global: false, ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnReceivedAsync(channel, delivery, buffer);
        // The broker cancels consumers if their queue is deleted; resubscribe as if the session was lost.
        // (Our own BasicCancel on shutdown also lands here, but nobody is waiting by then.)
        consumer.UnregisteredAsync += (_, _) =>
        {
            closed.TrySetResult("consumer cancelled");
            return Task.CompletedTask;
        };
        _consumerTag = await channel.BasicConsumeAsync(ClickTopology.Queue, autoAck: false, consumer, ct);

        logger.LogInformation("Consuming {Queue} on {Host} with prefetch {Prefetch}",
            ClickTopology.Queue, _factory.HostName, _prefetch);
        return closed.Task;
    }

    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs delivery, ChannelWriter<ClickDelivery> buffer)
    {
        if (!ClickParser.TryParse(delivery.Body.Span, out var click, out var error))
        {
            // requeue: false sends it to clicks.analytics.dlq via the queue's dead-letter exchange.
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false);
            metrics.DeadLettered();
            logger.LogWarning("Dead-lettered a malformed click message ({Bytes} bytes): {Error}",
                delivery.Body.Length, error);
            return;
        }

        // Fails only once shutdown has completed the buffer. The message then stays unacked and the broker
        // requeues it when the channel closes.
        buffer.TryWrite(new ClickDelivery(
            channel,
            delivery.DeliveryTag,
            click,
            MessagePropagation.Extract(delivery.BasicProperties.Headers),
            Stopwatch.GetTimestamp()));
    }

    // Stops new deliveries but keeps the channel open for the final ack.
    private async Task CancelConsumerAsync()
    {
        if (_channel is not { IsOpen: true } channel || _consumerTag is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await channel.BasicCancelAsync(_consumerTag, noWait: false, timeout.Token);
            logger.LogInformation("Stopped consuming {Queue}", ClickTopology.Queue);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not cancel the RabbitMQ consumer");
        }
    }
}
