using Feedback.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Seed;

/// <summary>
/// The data tables of the <c>feedback</c> schema, found in the catalog (so tables added by later migrations are
/// included without listing them). The migration history is left out: emptying it would make the next deploy re-run
/// every migration. Nothing here drops a table, the schema or the vector extension.
/// </summary>
// Raw SQL only ever holds catalog table names, quoted as identifiers by Quote().
#pragma warning disable EF1002
public static class FeedbackTables
{
    public const string MigrationHistory = "__EFMigrationsHistory";

    public static async Task<IReadOnlyList<string>> ListAsync(FeedbackDbContext db, CancellationToken ct) =>
        await db.Database.SqlQuery<string>($"""
            SELECT tablename::text AS "Value" FROM pg_catalog.pg_tables
            WHERE schemaname::text = {FeedbackDbContext.Schema} AND tablename::text <> {MigrationHistory}
            ORDER BY 1
            """).ToListAsync(ct);

    /// <summary>Row count per data table.</summary>
    public static async Task<IReadOnlyList<(string Table, long Rows)>> CountAsync(FeedbackDbContext db, CancellationToken ct)
    {
        var counts = new List<(string, long)>();
        foreach (var table in await ListAsync(db, ct))
            counts.Add((table, await db.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM {Qualified(table)}").SingleAsync(ct)));
        return counts;
    }

    /// <summary>Empties every data table in one transaction and returns how many rows each had.</summary>
    public static async Task<IReadOnlyList<(string Table, long Rows)>> EmptyAsync(FeedbackDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var counts = await CountAsync(db, ct);
        if (counts.Count > 0)
            // No CASCADE: if a table outside the schema ever referenced one of ours, this fails instead of emptying it.
            await db.Database.ExecuteSqlRawAsync(
                $"TRUNCATE {string.Join(", ", counts.Select(c => Qualified(c.Table)))} RESTART IDENTITY", ct);
        await transaction.CommitAsync(ct);
        return counts;
    }

    private static string Qualified(string table) => $"{Quote(FeedbackDbContext.Schema)}.{Quote(table)}";

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
