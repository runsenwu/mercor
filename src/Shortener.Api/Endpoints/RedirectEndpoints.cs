using Microsoft.AspNetCore.Http.HttpResults;
using Shortener.Api.Clicks;
using Shortener.Api.Links;
using Shortener.Api.Telemetry;

namespace Shortener.Api.Endpoints;

internal static class RedirectEndpoints
{
    public static void MapRedirects(this IEndpointRouteBuilder app) =>
        app.MapGet("/{code}", FollowAsync)
            .WithName("FollowLink").WithTags("Redirect")
            .WithSummary("Follow a short link")
            .WithDescription("302 to the long URL. 404 if unknown, 410 if deleted or expired. Never rate-limited.")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone);

    // Hot path: no Information logging, no allocations beyond what the lookup needs.
    private static async Task<Results<RedirectHttpResult, ProblemHttpResult>> FollowAsync(
        string code, LinkService links, IClickRecorder clicks, ApiMetrics metrics, HttpContext http)
    {
        var resolution = LinkRules.IsWellFormedCode(code)
            ? await links.ResolveAsync(code, http.RequestAborted)
            : new Resolution(LinkState.NotFound, null);

        switch (resolution.State)
        {
            case LinkState.Found:
                metrics.Redirect("found");
                await clicks.RecordAsync(code);
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Redirect(resolution.Url!);
            case LinkState.Gone:
                metrics.Redirect("gone");
                return TypedResults.Problem(statusCode: 410, title: "Link is gone", detail: "This link was deleted or has expired.");
            default:
                metrics.Redirect("not_found");
                return TypedResults.Problem(statusCode: 404, title: "Link not found");
        }
    }
}
