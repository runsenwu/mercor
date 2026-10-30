using System.Diagnostics;
using RabbitMQ.Client;
using Shortener.Shared;

namespace Shortener.Worker;

/// <summary>A parsed click waiting in the in-process buffer, with what is needed to ack it later.</summary>
/// <param name="Channel">
/// The channel the message arrived on. Delivery tags are only valid there, and if that channel has closed
/// the broker has already requeued the message.
/// </param>
/// <param name="ReceivedTimestamp"><see cref="Stopwatch"/> timestamp; drives <c>BatchMaxWaitMs</c>.</param>
internal readonly record struct ClickDelivery(
    IChannel Channel,
    ulong DeliveryTag,
    ClickEvent Click,
    ActivityContext TraceContext,
    long ReceivedTimestamp);
