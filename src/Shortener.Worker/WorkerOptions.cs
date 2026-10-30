namespace Shortener.Worker;

/// <summary>Bound from the <c>Worker</c> config section (env <c>Worker__*</c>).</summary>
internal sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// RabbitMQ QoS prefetch: the most unacked messages the broker hands this consumer. This is also what bounds
    /// the in-process buffer, so it should be at least <see cref="BatchSize"/> to let batches fill up.
    /// </summary>
    public int Prefetch { get; set; } = 1000;

    /// <summary>Flush when this many clicks are buffered...</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>...or when the oldest buffered click has waited this long.</summary>
    public int BatchMaxWaitMs { get; set; } = 1000;

    public TimeSpan BatchMaxWait => TimeSpan.FromMilliseconds(BatchMaxWaitMs);
}
