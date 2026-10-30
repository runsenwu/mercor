using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shortener.Shared;

/// <summary>
/// Published by the API for every successful redirect; consumed by the analytics worker.
/// The W3C trace context travels in the AMQP message headers, not in the body.
/// </summary>
public sealed record ClickEvent(string Code, DateTimeOffset At);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ClickEvent))]
public sealed partial class SharedJsonContext : JsonSerializerContext;
