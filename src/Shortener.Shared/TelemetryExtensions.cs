using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Shortener.Shared;

public static class TelemetryExtensions
{
    /// <summary>
    /// Wires logs, metrics and traces to OTLP (endpoint from OTEL_EXPORTER_OTLP_ENDPOINT).
    /// Each service adds its own instrumentation through the callbacks.
    /// </summary>
    public static IHostApplicationBuilder AddShortenerTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        // Head sampling in the app; the collector additionally keeps all errors and slow traces.
        var sampleRatio = builder.Configuration.GetValue("Telemetry:TraceSampleRatio", 1.0);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName, serviceInstanceId: Environment.MachineName))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter("System.Runtime");
                metrics.SetExemplarFilter(ExemplarFilterType.TraceBased);
                configureMetrics?.Invoke(metrics);
            })
            .WithTracing(tracing =>
            {
                tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio)));
                configureTracing?.Invoke(tracing);
            })
            .WithLogging(configureBuilder: null, configureOptions: options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            })
            .UseOtlpExporter();

        return builder;
    }
}
