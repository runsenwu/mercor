using System.Diagnostics;
using System.Text.Json;
using RabbitMQ.Client;
using Shortener.Api.Telemetry;
using Shortener.Shared;

namespace Shortener.Api.Clicks;

/// <summary>
/// Drains the click buffer into RabbitMQ over one long-lived connection + channel, reconnecting with backoff.
/// While RabbitMQ is down the buffer fills and further clicks are dropped (and counted).
/// </summary>
internal sealed class ClickPublisher(ClickBuffer buffer, IConfiguration config, ApiMetrics metrics,
    ILogger<ClickPublisher> logger) : BackgroundService
{
    private volatile IConnection? _connection;

    public bool IsConnected => _connection?.IsOpen == true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(config.GetConnectionString("RabbitMq") ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq missing")),
            // One reconnect path (ours) instead of mixing it with the client's auto-recovery.
            AutomaticRecoveryEnabled = false,
            ClientProvidedName = $"shortener-api:{Environment.MachineName}",
        };
        var backoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                // Publisher confirms are off on purpose: clicks are best-effort analytics, and waiting for
                // broker acks would cut throughput for a guarantee we don't need (a redirect never fails on analytics).
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await ClickTopology.DeclareAsync(channel, stoppingToken);
                _connection = connection;
                backoff = TimeSpan.FromSeconds(1);
                logger.LogInformation("Click publisher connected to RabbitMQ");

                while (await buffer.Reader.WaitToReadAsync(stoppingToken))
                    while (buffer.Reader.TryRead(out var click))
                        await PublishAsync(channel, click, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Click publisher disconnected; retrying in {Backoff}", backoff);
            }
            finally
            {
                _connection = null;
            }

            try { await Task.Delay(backoff, stoppingToken); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
        }
    }

    private async Task PublishAsync(IChannel channel, PendingClick click, CancellationToken ct)
    {
        // Producer span parented to the redirect, so the trace continues into the worker.
        using var activity = click.Parent == default ? null
            : ApiDiagnostics.Source.StartActivity($"publish {ClickTopology.Exchange}", ActivityKind.Producer, click.Parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.type", "send");
        activity?.SetTag("messaging.destination.name", ClickTopology.Exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", ClickTopology.RoutingKey);

        var headers = new Dictionary<string, object?>(2);
        MessagePropagation.Inject(activity?.Context ?? click.Parent, headers);
        var props = new BasicProperties { Persistent = true, ContentType = "application/json", Headers = headers };
        var body = JsonSerializer.SerializeToUtf8Bytes(new ClickEvent(click.Code, click.At), SharedJsonContext.Default.ClickEvent);
        try
        {
            await channel.BasicPublishAsync(ClickTopology.Exchange, ClickTopology.RoutingKey, false, props, body, ct);
            metrics.ClickPublished();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.ClickPublishFailed();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
