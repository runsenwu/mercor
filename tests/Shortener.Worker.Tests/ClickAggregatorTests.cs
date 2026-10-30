using Shortener.Shared;

namespace Shortener.Worker.Tests;

public class ClickAggregatorTests
{
    private static ClickEvent Click(string code, string at) => new(code, DateTimeOffset.Parse(at));

    [Fact]
    public void Empty_batch_gives_no_rows()
    {
        var result = ClickAggregator.Aggregate([]);

        Assert.Empty(result.PerCode);
        Assert.Empty(result.PerDay);
    }

    [Fact]
    public void Counts_clicks_per_code_and_per_code_and_day()
    {
        var result = ClickAggregator.Aggregate(
        [
            Click("abc1234", "2026-10-30T08:00:00Z"),
            Click("xyz9876", "2026-10-30T09:00:00Z"),
            Click("abc1234", "2026-10-30T10:00:00Z"),
            Click("abc1234", "2026-10-31T10:00:00Z"),
        ]);

        Assert.Equal([new CodeClicks("abc1234", 3), new CodeClicks("xyz9876", 1)], result.PerCode);
        Assert.Equal(
        [
            new DailyClicks("abc1234", new DateOnly(2026, 10, 30), 2),
            new DailyClicks("abc1234", new DateOnly(2026, 10, 31), 1),
            new DailyClicks("xyz9876", new DateOnly(2026, 10, 30), 1),
        ], result.PerDay);
    }

    [Theory]
    // Late evening in New York is already the next day in UTC.
    [InlineData("2026-10-30T23:30:00-04:00", 2026, 10, 31)]
    // Just after midnight in Berlin is still the previous day in UTC.
    [InlineData("2026-10-31T00:30:00+01:00", 2026, 10, 30)]
    [InlineData("2026-10-30T23:59:59.999Z", 2026, 10, 30)]
    [InlineData("2026-10-31T00:00:00Z", 2026, 10, 31)]
    public void Buckets_by_utc_day(string at, int year, int month, int day)
    {
        var result = ClickAggregator.Aggregate([Click("abc1234", at)]);

        Assert.Equal(new DateOnly(year, month, day), Assert.Single(result.PerDay).Day);
    }

    [Fact]
    public void Clicks_in_different_offsets_on_the_same_utc_day_share_a_row()
    {
        var result = ClickAggregator.Aggregate(
        [
            Click("abc1234", "2026-10-30T22:00:00-01:00"), // 23:00Z
            Click("abc1234", "2026-10-31T01:00:00+02:00"), // 23:00Z the day before
        ]);

        Assert.Equal(new DailyClicks("abc1234", new DateOnly(2026, 10, 30), 2), Assert.Single(result.PerDay));
    }

    [Fact]
    public void Codes_are_case_sensitive_and_sorted_ordinally()
    {
        var result = ClickAggregator.Aggregate(
        [
            Click("b", "2026-10-30T00:00:00Z"),
            Click("a", "2026-10-30T00:00:00Z"),
            Click("B", "2026-10-30T00:00:00Z"),
            Click("_x", "2026-10-30T00:00:00Z"),
            Click("a", "2026-10-30T00:00:00Z"),
            Click("0", "2026-10-30T00:00:00Z"),
            Click("-y", "2026-10-30T00:00:00Z"),
        ]);

        // Ordinal: '-' < '0' < 'B' < '_' < 'a' < 'b' (a culture-aware sort would differ).
        Assert.Equal(["-y", "0", "B", "_x", "a", "b"], result.PerCode.Select(r => r.Code));
        Assert.Equal(2, result.PerCode.Single(r => r.Code == "a").Clicks);
    }

    [Fact]
    public void Daily_rows_are_sorted_by_code_then_day_regardless_of_input_order()
    {
        var result = ClickAggregator.Aggregate(
        [
            Click("zzz", "2026-10-29T12:00:00Z"),
            Click("aaa", "2026-10-31T12:00:00Z"),
            Click("zzz", "2026-10-28T12:00:00Z"),
            Click("aaa", "2026-10-29T12:00:00Z"),
            Click("aaa", "2026-10-30T12:00:00Z"),
        ]);

        Assert.Equal(
        [
            ("aaa", new DateOnly(2026, 10, 29)),
            ("aaa", new DateOnly(2026, 10, 30)),
            ("aaa", new DateOnly(2026, 10, 31)),
            ("zzz", new DateOnly(2026, 10, 28)),
            ("zzz", new DateOnly(2026, 10, 29)),
        ], result.PerDay.Select(r => (r.Code, r.Day)));
    }

    [Fact]
    public void Totals_match_input_size()
    {
        var clicks = Enumerable.Range(0, 1000)
            .Select(i => new ClickEvent($"c{i % 37}", DateTimeOffset.UnixEpoch.AddHours(i)))
            .ToList();

        var result = ClickAggregator.Aggregate(clicks);

        Assert.Equal(37, result.PerCode.Length);
        Assert.Equal(1000, result.PerCode.Sum(r => r.Clicks));
        Assert.Equal(1000, result.PerDay.Sum(r => r.Clicks));
        Assert.Equal(result.PerDay.Length, result.PerDay.Select(r => (r.Code, r.Day)).Distinct().Count());
    }
}
