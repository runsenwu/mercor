using System.Diagnostics;
using Shortener.Shared;

namespace Shortener.Worker;

internal static class WorkerTelemetry
{
    public static readonly ActivitySource Source = new(DiagnosticNames.Worker);
}
