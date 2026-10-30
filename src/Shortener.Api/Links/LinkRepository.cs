using Dapper;
using Npgsql;

namespace Shortener.Api.Links;

internal sealed class LinkRow
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string LongUrl { get; set; } = "";
    public string? Owner { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public long ClickCount { get; set; }
}

internal sealed class DailyRow
{
    public DateTime Day { get; set; }
    public long Clicks { get; set; }
}

internal sealed class LinkRepository(NpgsqlDataSource db)
{
    private const string Columns = "id AS Id, code AS Code, long_url AS LongUrl, owner AS Owner, created_at AS CreatedAt, " +
                                   "expires_at AS ExpiresAt, deleted_at AS DeletedAt, click_count AS ClickCount";

    /// <summary>Returns null when the code is already taken (unique violation).</summary>
    public async Task<LinkRow?> TryInsertAsync(string code, string url, string? owner, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        await using var conn = db.CreateConnection();
        try
        {
            return await conn.QuerySingleAsync<LinkRow>(new CommandDefinition(
                $"INSERT INTO links (code, long_url, owner, expires_at) VALUES (@code, @url, @owner, @expiresAt) RETURNING {Columns}",
                new { code, url, owner, expiresAt = expiresAt?.ToUniversalTime() }, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
    }

    public async Task<LinkRow?> GetAsync(string code, CancellationToken ct)
    {
        await using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<LinkRow>(new CommandDefinition(
            $"SELECT {Columns} FROM links WHERE code = @code", new { code }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DailyRow>> GetDailyAsync(string code, CancellationToken ct)
    {
        await using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<DailyRow>(new CommandDefinition(
            """
            SELECT day AS Day, clicks AS Clicks FROM link_clicks_daily
            WHERE code = @code AND day > (now() AT TIME ZONE 'UTC')::date - 30 ORDER BY day
            """, new { code }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Keyset page, newest first; served by the partial index links_owner_id_idx.</summary>
    public async Task<IReadOnlyList<LinkRow>> ListByOwnerAsync(string owner, long beforeId, int limit, CancellationToken ct)
    {
        await using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<LinkRow>(new CommandDefinition(
            $"SELECT {Columns} FROM links WHERE owner = @owner AND deleted_at IS NULL AND id < @beforeId ORDER BY id DESC LIMIT @limit",
            new { owner, beforeId, limit }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SoftDeleteAsync(string code, CancellationToken ct)
    {
        await using var conn = db.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE links SET deleted_at = now() WHERE code = @code AND deleted_at IS NULL", new { code }, cancellationToken: ct));
    }
}
