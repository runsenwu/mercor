using Shortener.Api.Links;

namespace Shortener.Api.Tests;

public class LinkRulesTests
{
    [Fact]
    public void Generated_codes_are_7_base62_chars_and_vary()
    {
        var codes = Enumerable.Range(0, 10_000).Select(_ => LinkRules.NewCode()).ToList();
        Assert.All(codes, c => Assert.Matches("^[0-9A-Za-z]{7}$", c));
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(62, codes.SelectMany(c => c).Distinct().Count());
    }

    [Theory]
    [InlineData("https://example.com/a?b=c", true)]
    [InlineData("http://example.com", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    [InlineData("http://localhost:8080/abc", false)]
    public void Validates_urls(string url, bool valid) =>
        Assert.Equal(valid, LinkRules.ValidateUrl(url, "localhost", out _) is null);

    [Fact]
    public void Rejects_too_long_urls() =>
        Assert.NotNull(LinkRules.ValidateUrl("https://e.com/" + new string('a', 2048), "localhost", out _));

    [Theory]
    [InlineData("my-link_1", true)]
    [InlineData("ab", false)]
    [InlineData("has space", false)]
    [InlineData("Docs", false)]
    [InlineData("healthz", false)]
    public void Validates_aliases(string alias, bool valid) => Assert.Equal(valid, LinkRules.ValidateAlias(alias) is null);

    [Theory]
    [InlineData(1L)]
    [InlineData(123456789L)]
    [InlineData(long.MaxValue)]
    public void Cursor_round_trips(long id)
    {
        Assert.True(LinkRules.TryDecodeCursor(LinkRules.EncodeCursor(id), out var decoded));
        Assert.Equal(id, decoded);
    }

    [Fact]
    public void Rejects_garbage_cursor() => Assert.False(LinkRules.TryDecodeCursor("not-a-cursor!", out _));

    [Fact]
    public void Cache_ttl_never_outlives_the_link()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var max = TimeSpan.FromHours(1);
        Assert.Equal(max, LinkRules.CacheTtl(null, now, max));
        Assert.Equal(max, LinkRules.CacheTtl(now.UtcDateTime.AddDays(1), now, max));
        Assert.Equal(TimeSpan.FromMinutes(5), LinkRules.CacheTtl(now.UtcDateTime.AddMinutes(5), now, max));
        Assert.Null(LinkRules.CacheTtl(now.UtcDateTime.AddSeconds(-1), now, max));
    }
}
