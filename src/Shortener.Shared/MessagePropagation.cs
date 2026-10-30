using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace Shortener.Shared;

/// <summary>
/// Carries W3C trace context (traceparent/tracestate) through AMQP message headers,
/// so a click's trace continues from the redirect into the worker.
/// </summary>
public static class MessagePropagation
{
    public static void Inject(ActivityContext context, IDictionary<string, object?> headers)
    {
        if (context == default) return;
        Propagators.DefaultTextMapPropagator.Inject(
            new PropagationContext(context, Baggage.Current), headers,
            static (carrier, key, value) => carrier[key] = value);
    }

    public static ActivityContext Extract(IDictionary<string, object?>? headers)
    {
        if (headers is null) return default;
        return Propagators.DefaultTextMapPropagator.Extract(default, headers, static (carrier, key) =>
            carrier.TryGetValue(key, out var value) ? value switch
            {
                byte[] bytes => [Encoding.UTF8.GetString(bytes)],
                string text => [text],
                _ => [],
            } : []).ActivityContext;
    }
}
