using Dapper;
using Npgsql;

namespace Shortener.Worker;

/// <summary>Applies one aggregated batch to Postgres in a single transaction.</summary>
internal sealed class ClickStore(NpgsqlDataSource dataSource)
{
    private const int CommandTimeoutSeconds = 15;

    // Lock order (several worker replicas update overlapping rows; inconsistent lock order deadlocks):
    // - link_clicks_daily: INSERT ... SELECT processes rows in array order, and ClickAggregator sorts them.
    // - links: the planner may run the UPDATE as a hash join (rows locked in heap order) for big batches and as a
    //   nested loop (array order) for small ones, so two replicas could still lock in different orders. The first
    //   statement therefore takes the row locks explicitly in code order (LockRows runs above the Sort).
    // All links locks are taken before any link_clicks_daily lock, so the two tables can't deadlock each other.
    private const string Sql = """
        SELECT 1 FROM links WHERE code = ANY(@codes) ORDER BY code FOR NO KEY UPDATE;

        UPDATE links SET click_count = links.click_count + v.n
        FROM unnest(@codes, @counts) AS v(code, n)
        WHERE links.code = v.code;

        INSERT INTO link_clicks_daily (code, day, clicks)
        SELECT * FROM unnest(@dayCodes, @days, @dayCounts)
        ON CONFLICT (code, day) DO UPDATE SET clicks = link_clicks_daily.clicks + EXCLUDED.clicks;
        """;

    public async Task WriteAsync(ClickAggregate batch, CancellationToken cancellationToken)
    {
        // Unknown codes simply match no links row; their daily rows are still written (no FK on purpose).
        var parameters = new
        {
            codes = batch.PerCode.Select(r => r.Code).ToArray(),
            counts = batch.PerCode.Select(r => r.Clicks).ToArray(),
            dayCodes = batch.PerDay.Select(r => r.Code).ToArray(),
            days = batch.PerDay.Select(r => r.Day).ToArray(),
            dayCounts = batch.PerDay.Select(r => r.Clicks).ToArray(),
        };

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Dapper hands arrays to Npgsql as Postgres arrays (text[], bigint[], date[]) rather than expanding them.
        // The three statements go out in one round trip.
        await connection.ExecuteAsync(new CommandDefinition(
            Sql, parameters, transaction, CommandTimeoutSeconds, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }
}
