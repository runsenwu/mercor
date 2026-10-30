using Npgsql;
using Shortener.Shared;
using Shortener.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddShortenerTelemetry(
    DiagnosticNames.WorkerService,
    metrics => metrics.AddMeter(DiagnosticNames.Worker, "Npgsql"),
    tracing => tracing.AddNpgsql().AddSource(DiagnosticNames.Worker));

builder.Services.AddOptions<WorkerOptions>()
    .BindConfiguration(WorkerOptions.SectionName)
    .Validate(o => o.Prefetch is > 0 and <= ushort.MaxValue, "Worker:Prefetch must be between 1 and 65535.")
    .Validate(o => o.BatchSize > 0, "Worker:BatchSize must be positive.")
    .Validate(o => o.BatchMaxWaitMs > 0, "Worker:BatchMaxWaitMs must be positive.")
    .ValidateOnStart();

// Room to stop consuming, store and ack the in-flight batch (incl. a retry or two) before the host gives up.
// The container's stop grace period must be longer than this.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

builder.Services.AddSingleton(sp => new NpgsqlDataSourceBuilder(
        builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."))
    .UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>())
    .Build());
builder.Services.AddSingleton<WorkerMetrics>();
builder.Services.AddSingleton<ClickStore>();
builder.Services.AddSingleton<ClickBatchProcessor>();
builder.Services.AddSingleton<ClickConsumer>();
builder.Services.AddHostedService<AnalyticsWorker>();

builder.Build().Run();
