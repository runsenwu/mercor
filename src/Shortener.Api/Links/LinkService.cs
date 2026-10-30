using Dapper;
using Npgsql;
using Shortener.Api.Telemetry;

namespace Shortener.Api.Links;

internal enum DeleteOutcome { Deleted, NotFound, NotOwner }

internal sealed class LinkService(LinkRepository repo, LinkCache cache, NpgsqlDataSource db, ApiMetrics metrics,
    TimeProvider time)
{
    private const int MaxCodeAttempts = 5;

    /// <summary>Redirect lookup: Redis first (unless disabled or down), then Postgres, then fill the cache.</summary>
    public async ValueTask<Resolution> ResolveAsync(string code, CancellationToken ct)
    {
        var useCache = cache.Enabled;
        if (useCache)
        {
            var (hit, available) = await cache.GetAsync(code);
            if (hit is { } cached) return cached;
            useCache = available;
        }

        await using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<LinkRow>(new CommandDefinition(
            "SELECT long_url AS LongUrl, expires_at AS ExpiresAt, deleted_at AS DeletedAt FROM links WHERE code = @code",
            new { code }, cancellationToken: ct));

        var now = time.GetUtcNow();
        var resolution = row is null ? new Resolution(LinkState.NotFound, null)
            : LinkRules.IsGone(row.DeletedAt, row.ExpiresAt, now) ? new Resolution(LinkState.Gone, null)
            : new Resolution(LinkState.Found, row.LongUrl);

        if (useCache) cache.Fill(code, resolution, LinkRules.CacheTtl(row?.ExpiresAt, now, cache.MaxTtl));
        return resolution;
    }

    /// <summary>Returns null when the requested alias is already taken.</summary>
    public async Task<LinkRow?> CreateAsync(string url, string? alias, DateTimeOffset? expiresAt, string? owner, CancellationToken ct)
    {
        LinkRow? row = null;
        if (alias is not null)
        {
            row = await repo.TryInsertAsync(alias, url, owner, expiresAt, ct);
            if (row is null) return null;
        }
        else
        {
            for (var attempt = 0; attempt < MaxCodeAttempts && row is null; attempt++)
            {
                var code = LinkRules.NewCode();
                if (LinkRules.IsReserved(code)) continue;
                row = await repo.TryInsertAsync(code, url, owner, expiresAt, ct);
                if (row is null) metrics.CodeCollision();
            }
            if (row is null) throw new InvalidOperationException($"No free code after {MaxCodeAttempts} attempts.");
        }

        metrics.LinkCreated(owner is null ? "anonymous" : "api_key");
        // Overwrites a cached ~404 from someone probing this code earlier, and warms the cache.
        var ttl = LinkRules.CacheTtl(row.ExpiresAt, time.GetUtcNow(), cache.MaxTtl);
        if (ttl is not null) await cache.SetAsync(row.Code, row.LongUrl, ttl);
        return row;
    }

    public async Task<DeleteOutcome> DeleteAsync(string code, string owner, CancellationToken ct)
    {
        var row = await repo.GetAsync(code, ct);
        if (row is null || row.DeletedAt is not null) return DeleteOutcome.NotFound;
        if (!string.Equals(row.Owner, owner, StringComparison.Ordinal)) return DeleteOutcome.NotOwner;
        await repo.SoftDeleteAsync(code, ct);
        // Tombstone instead of DEL: a concurrent redirect's SET NX can't re-insert the old URL.
        await cache.SetAsync(code, null, null);
        return DeleteOutcome.Deleted;
    }
}
