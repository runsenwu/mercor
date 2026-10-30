using Shortener.Shared;

namespace Shortener.Worker;

internal readonly record struct CodeClicks(string Code, long Clicks);

internal readonly record struct DailyClicks(string Code, DateOnly Day, long Clicks);

/// <param name="PerCode">Increments for <c>links.click_count</c>, sorted by code (ordinal).</param>
/// <param name="PerDay">Increments for <c>link_clicks_daily</c>, sorted by code (ordinal), then day.</param>
internal sealed record ClickAggregate(CodeClicks[] PerCode, DailyClicks[] PerDay);

/// <summary>Collapses a batch of clicks into one row per code and one row per (code, UTC day).</summary>
internal static class ClickAggregator
{
    public static ClickAggregate Aggregate(IEnumerable<ClickEvent> clicks)
    {
        // Codes are case-sensitive (base62), so compare ordinally everywhere.
        var perCode = new Dictionary<string, long>(StringComparer.Ordinal);
        var perDay = new Dictionary<(string Code, DateOnly Day), long>();

        foreach (var click in clicks)
        {
            perCode[click.Code] = perCode.GetValueOrDefault(click.Code) + 1;

            // Bucket by the UTC calendar day, whatever offset the publisher used.
            var key = (click.Code, DateOnly.FromDateTime(click.At.UtcDateTime));
            perDay[key] = perDay.GetValueOrDefault(key) + 1;
        }

        // The order matters: Postgres takes row locks in the order it processes the rows, and several worker
        // replicas update overlapping rows. Two transactions locking the same rows in different orders deadlock,
        // so every replica writes in the same (sorted) order. Aggregating also guarantees each (code, day)
        // appears once, which ON CONFLICT DO UPDATE requires.
        var codeRows = perCode
            .Select(kv => new CodeClicks(kv.Key, kv.Value))
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .ToArray();
        var dayRows = perDay
            .Select(kv => new DailyClicks(kv.Key.Code, kv.Key.Day, kv.Value))
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .ThenBy(r => r.Day)
            .ToArray();

        return new ClickAggregate(codeRows, dayRows);
    }
}
