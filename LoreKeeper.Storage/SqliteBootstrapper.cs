using Dapper;
using Microsoft.Data.Sqlite;

namespace LoreKeeper.Storage;

public sealed class SqliteBootstrapper : IDbBootstrapper
{
    private readonly StorageConfig _cfg;
    public SqliteBootstrapper(StorageConfig cfg) => _cfg = cfg;

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory("./data");
        await using var conn = new SqliteConnection(_cfg.ConnectionString);
        await conn.OpenAsync(ct);

        // Ensure FKs are enforced for this connection
        await conn.ExecuteAsync("PRAGMA foreign_keys = ON;");

        if (_cfg.DropOnStart && _cfg.TruncateOnStart)
            throw new InvalidOperationException("Storage: DropOnStart and TruncateOnStart cannot both be true.");

        // If requested, drop tables first (child before parent), then recreate schema
        if (_cfg.DropOnStart)
        {
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                await conn.ExecuteAsync("""
                    DROP TABLE IF EXISTS PageBodies;
                    DROP TABLE IF EXISTS Pages;
                """, transaction: tx);
                await tx.CommitAsync(ct);
            }
        }

        // Create/ensure schema
        var createSql = """
            CREATE TABLE IF NOT EXISTS Pages(
              Wiki TEXT NOT NULL,
              PageId INTEGER NOT NULL,
              Title TEXT NOT NULL,
              RevId INTEGER NULL,
              LastFetchedUtc TEXT NOT NULL,
              PRIMARY KEY (Wiki, PageId)
            );

            CREATE TABLE IF NOT EXISTS PageBodies(
              Wiki TEXT NOT NULL,
              PageId INTEGER NOT NULL,
              Format TEXT NOT NULL,  -- 'html' or 'wikitext'
              Body   TEXT NOT NULL,
              FetchedUtc TEXT NOT NULL,
              PRIMARY KEY (Wiki, PageId, Format),
              FOREIGN KEY (Wiki, PageId) REFERENCES Pages(Wiki, PageId) ON DELETE CASCADE
            );
        """;
        await conn.ExecuteAsync(createSql);

        // If requested, truncate data (delete all rows). FK cascade handles PageBodies via Pages delete.
        if (_cfg.TruncateOnStart)
        {
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                // Deleting from Pages is sufficient; cascade will remove PageBodies
                await conn.ExecuteAsync("DELETE FROM Pages;", transaction: tx);
                await tx.CommitAsync(ct);
            }

            // Reclaim space (cannot run inside a transaction)
            await conn.ExecuteAsync("VACUUM;");
        }
    }
}
