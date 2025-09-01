using Dapper;
using Microsoft.Data.Sqlite;
using System.Data;

namespace LoreKeeper.Storage;

public interface IDbBootstrapper
{
    Task InitializeAsync(CancellationToken ct);
}

public sealed class SqliteBootstrapper : IDbBootstrapper
{
    private readonly StorageConfig _cfg;
    public SqliteBootstrapper(StorageConfig cfg) => _cfg = cfg;

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory("./data");
        await using var conn = new SqliteConnection(_cfg.ConnectionString);
        await conn.OpenAsync(ct);

        var sql = """

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
        await conn.ExecuteAsync(sql);
    }
}

public sealed record PageRow
{
    public string Wiki { get; init; } = "";
    public int PageId { get; init; }
    public string Title { get; init; } = "";
    public int? RevId { get; init; }
    public DateTimeOffset LastFetchedUtc { get; init; }
}

public sealed record PageBodyRow
{
    public string Wiki { get; init; } = "";
    public int PageId { get; init; }
    public string Format { get; init; } = "html";
    public string Body { get; init; } = "";
    public DateTimeOffset FetchedUtc { get; init; }
}

public interface IPagesRepository
{
    Task UpsertPageAsync(PageRow row);
    Task UpsertBodyAsync(PageBodyRow row);
}

public sealed class PagesRepository : IPagesRepository
{
    private readonly StorageConfig _cfg;
    public PagesRepository(StorageConfig cfg) => _cfg = cfg;

    public async Task UpsertPageAsync(PageRow row)
    {
        await using var conn = new SqliteConnection(_cfg.ConnectionString);
        await conn.OpenAsync();

        var sql = """

                  INSERT INTO Pages (Wiki, PageId, Title, RevId, LastFetchedUtc)
                  VALUES (@Wiki, @PageId, @Title, @RevId, @LastFetchedUtc)
                  ON CONFLICT(Wiki, PageId) DO UPDATE SET
                    Title = excluded.Title,
                    RevId = excluded.RevId,
                    LastFetchedUtc = excluded.LastFetchedUtc;

                  """;
        await conn.ExecuteAsync(sql, row);
    }

    public async Task UpsertBodyAsync(PageBodyRow row)
    {
        await using var conn = new SqliteConnection(_cfg.ConnectionString);
        await conn.OpenAsync();

        var sql = """

                  INSERT INTO PageBodies (Wiki, PageId, Format, Body, FetchedUtc)
                  VALUES (@Wiki, @PageId, @Format, @Body, @FetchedUtc)
                  ON CONFLICT(Wiki, PageId, Format) DO UPDATE SET
                    Body = excluded.Body,
                    FetchedUtc = excluded.FetchedUtc;

                  """;
        await conn.ExecuteAsync(sql, row);
    }
}