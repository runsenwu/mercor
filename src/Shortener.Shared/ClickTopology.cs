using RabbitMQ.Client;

namespace Shortener.Shared;

/// <summary>
/// RabbitMQ topology for click events. Both the API and the worker declare it on startup;
/// declarations are idempotent as long as the arguments match, so they must only change here.
/// </summary>
public static class ClickTopology
{
    public const string Exchange = "clicks";
    public const string RoutingKey = "click";
    public const string Queue = "clicks.analytics";
    public const string DeadLetterExchange = "clicks.dlx";
    public const string DeadLetterQueue = "clicks.analytics.dlq";

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true,
            autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, routingKey: "",
            cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadLetterExchange },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, Exchange, RoutingKey, cancellationToken: cancellationToken);
    }
}
