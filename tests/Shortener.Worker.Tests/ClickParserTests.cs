using System.Text;

namespace Shortener.Worker.Tests;

public class ClickParserTests
{
    private static bool Parse(string json, out Shared.ClickEvent? click, out string? error) =>
        ClickParser.TryParse(Encoding.UTF8.GetBytes(json), out click, out error);

    [Fact]
    public void Parses_the_contract_shape()
    {
        Assert.True(Parse("""{"code":"abc1234","at":"2026-10-30T12:00:00Z"}""", out var click, out _));

        Assert.Equal("abc1234", click!.Code);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), click.At);
    }

    [Fact]
    public void Accepts_a_code_of_maximum_length()
    {
        var code = new string('a', ClickParser.MaxCodeLength);

        Assert.True(Parse($$"""{"code":"{{code}}","at":"2026-10-30T12:00:00Z"}""", out _, out _));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"code":"abc1234","at":"yesterday"}""")]
    [InlineData("""{"at":"2026-10-30T12:00:00Z"}""")]
    [InlineData("""{"code":"","at":"2026-10-30T12:00:00Z"}""")]
    [InlineData("""{"code":"abc1234"}""")]
    [InlineData("""{"code":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","at":"2026-10-30T12:00:00Z"}""")] // 33 chars
    public void Rejects_malformed_bodies(string json)
    {
        Assert.False(Parse(json, out var click, out var error));

        Assert.Null(click);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
