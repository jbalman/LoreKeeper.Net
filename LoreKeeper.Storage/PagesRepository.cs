using Dapper;
using LoreKeeper.Storage.Models;
using Microsoft.Data.Sqlite;

namespace LoreKeeper.Storage;

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