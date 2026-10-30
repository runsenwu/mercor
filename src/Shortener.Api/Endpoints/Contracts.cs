using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Shortener.Api.Endpoints;

public sealed record CreateLinkRequest(
    [property: Required, Description("Absolute http/https URL to redirect to (max 2048 chars).")] string? Url,
    [property: Description("Optional custom code: 3-32 chars of [A-Za-z0-9_-], not a reserved word.")] string? Alias = null,
    [property: Description("Optional ISO-8601 expiry; must be in the future.")] DateTimeOffset? ExpiresAt = null);

public sealed record LinkResponse(string Code, string ShortUrl, string Url, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, long Clicks, [property: Description("API key name; null for anonymous links.")] string? Owner);

public sealed record DailyClicks(DateOnly Date, long Clicks);

public sealed record LinkStats(string Code, long TotalClicks,
    [property: Description("Last 30 days (UTC), oldest first; days without clicks are omitted.")] IReadOnlyList<DailyClicks> Daily);

public sealed record LinkPage(IReadOnlyList<LinkResponse> Items,
    [property: Description("Pass as ?cursor= to get the next page; null on the last page.")] string? NextCursor);
