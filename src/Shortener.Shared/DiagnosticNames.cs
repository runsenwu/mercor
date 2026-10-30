namespace Shortener.Shared;

/// <summary>
/// Names used for OpenTelemetry resources, meters and activity sources.
/// Dashboards and alerts depend on these, so treat them as a public contract.
/// </summary>
public static class DiagnosticNames
{
    public const string ApiService = "shortener-api";
    public const string WorkerService = "shortener-worker";

    // Each service owns one Meter and one ActivitySource with the same name.
    public const string Api = "Shortener.Api";
    public const string Worker = "Shortener.Worker";
}
