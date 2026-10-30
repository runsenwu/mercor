using System.ComponentModel;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Shortener.Api.Auth;
using Shortener.Api.Links;
using Shortener.Api.RateLimiting;

namespace Shortener.Api.Endpoints;

internal static class LinkEndpoints
{
    public static void MapLinkApi(this IEndpointRouteBuilder app)
    {
        var links = app.MapGroup("/api/v1/links").WithTags("Links")
            .AddEndpointFilter<ApiKeyFilter>()
            .AddEndpointFilter<RateLimitFilter>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        links.MapPost("", CreateAsync).WithName("CreateLink").WithSummary("Create a short link")
            .WithDescription("Anonymous or with X-Api-Key. Random 7-char code unless an alias is given.")
            .ProducesProblem(StatusCodes.Status409Conflict);
        links.MapGet("{code}", GetAsync).WithName("GetLink").WithSummary("Get a link")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);
        links.MapGet("{code}/stats", GetStatsAsync).WithName("GetLinkStats").WithSummary("Click statistics for a link")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);
        links.MapGet("", ListAsync).WithName("ListLinks").WithSummary("List your links (API key required), newest first")
            .WithMetadata(new RequireApiKeyMetadata());
        links.MapDelete("{code}", DeleteAsync).WithName("DeleteLink").WithSummary("Delete one of your links (API key required)")
            .WithMetadata(new RequireApiKeyMetadata())
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Results<Created<LinkResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateLinkRequest request, Caller caller, LinkService service, IOptions<ShortenerOptions> options,
        TimeProvider time, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var ownHost = new Uri(options.Value.PublicBaseUrl).IdnHost;
        if (LinkRules.ValidateUrl(request.Url, ownHost, out var url) is { } urlError) errors["url"] = [urlError];
        if (request.Alias is not null && LinkRules.ValidateAlias(request.Alias) is { } aliasError) errors["alias"] = [aliasError];
        if (request.ExpiresAt is { } exp && exp <= time.GetUtcNow()) errors["expiresAt"] = ["expiresAt must be in the future."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var row = await service.CreateAsync(url, request.Alias, request.ExpiresAt, caller.KeyName, ct);
        if (row is null)
            return TypedResults.Problem(statusCode: 409, title: "Alias taken", detail: $"The alias '{request.Alias}' is already in use.");
        return TypedResults.Created($"/api/v1/links/{row.Code}", ToResponse(row, options.Value));
    }

    private static async Task<Results<Ok<LinkResponse>, ProblemHttpResult>> GetAsync(
        string code, LinkRepository repo, IOptions<ShortenerOptions> options, TimeProvider time, CancellationToken ct)
    {
        var row = LinkRules.IsWellFormedCode(code) ? await repo.GetAsync(code, ct) : null;
        if (row is null) return NotFound(code);
        if (LinkRules.IsGone(row.DeletedAt, row.ExpiresAt, time.GetUtcNow())) return Gone(code);
        return TypedResults.Ok(ToResponse(row, options.Value));
    }

    private static async Task<Results<Ok<LinkStats>, ProblemHttpResult>> GetStatsAsync(
        string code, LinkRepository repo, TimeProvider time, CancellationToken ct)
    {
        var row = LinkRules.IsWellFormedCode(code) ? await repo.GetAsync(code, ct) : null;
        if (row is null) return NotFound(code);
        if (LinkRules.IsGone(row.DeletedAt, row.ExpiresAt, time.GetUtcNow())) return Gone(code);
        var daily = await repo.GetDailyAsync(code, ct);
        return TypedResults.Ok(new LinkStats(row.Code, row.ClickCount,
            daily.Select(d => new DailyClicks(DateOnly.FromDateTime(d.Day), d.Clicks)).ToList()));
    }

    private static async Task<Results<Ok<LinkPage>, ValidationProblem>> ListAsync(
        Caller caller, LinkRepository repo, IOptions<ShortenerOptions> options,
        [Description("Page size, 1-100 (default 20).")] int? limit,
        [Description("Opaque cursor from the previous page's nextCursor.")] string? cursor,
        CancellationToken ct)
    {
        var size = limit ?? 20;
        var beforeId = long.MaxValue;
        var errors = new Dictionary<string, string[]>();
        if (size is < 1 or > 100) errors["limit"] = ["limit must be between 1 and 100."];
        if (cursor is not null && !LinkRules.TryDecodeCursor(cursor, out beforeId)) errors["cursor"] = ["Invalid cursor."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        // Fetch one extra row to know whether there is a next page.
        var rows = await repo.ListByOwnerAsync(caller.KeyName!, beforeId, size + 1, ct);
        var items = rows.Take(size).Select(r => ToResponse(r, options.Value)).ToList();
        var next = rows.Count > size ? LinkRules.EncodeCursor(rows[size - 1].Id) : null;
        return TypedResults.Ok(new LinkPage(items, next));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        string code, Caller caller, LinkService service, CancellationToken ct)
    {
        if (!LinkRules.IsWellFormedCode(code)) return NotFound(code);
        return await service.DeleteAsync(code, caller.KeyName!, ct) switch
        {
            DeleteOutcome.Deleted => TypedResults.NoContent(),
            DeleteOutcome.NotOwner => TypedResults.Problem(statusCode: 403, title: "Not the owner",
                detail: "Only the API key that created a link can delete it."),
            _ => NotFound(code),
        };
    }

    private static ProblemHttpResult NotFound(string code) =>
        TypedResults.Problem(statusCode: 404, title: "Link not found", detail: $"No link with code '{code}'.");

    private static ProblemHttpResult Gone(string code) =>
        TypedResults.Problem(statusCode: 410, title: "Link is gone", detail: $"The link '{code}' was deleted or has expired.");

    private static LinkResponse ToResponse(LinkRow row, ShortenerOptions options) => new(
        row.Code, options.PublicBaseUrl.TrimEnd('/') + "/" + row.Code, row.LongUrl,
        new DateTimeOffset(row.CreatedAt, TimeSpan.Zero),
        row.ExpiresAt is { } e ? new DateTimeOffset(e, TimeSpan.Zero) : null,
        row.ClickCount, row.Owner);
}
