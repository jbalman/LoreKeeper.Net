perfect — here’s a **single, drop-in file** that contains everything you asked for:

* `CrawlService` (full page generation, category discovery, skip unchanged, per-run dedup, end-of-run duplicate cleanup)
* `CrawlerConfig` & `WikiSite`
* Interfaces: `IWikiClient`, `IStorage`
* Implementations:

  * `MediaWikiClient` (uses MediaWiki API)
  * `SqliteStorage` (Dapper + Microsoft.Data.Sqlite; includes schema bootstrap)
* DI extension: `AddCrawlerServices(...)` so you can wire this up cleanly from `Program.cs`

> Save this as **`LoreKeeper.Crawler.All.cs`** inside your `LoreKeeper.Crawler` project.
> You already have `appsettings.json`; this file binds to `Crawler` section (same keys we used together earlier).

---

```csharp
// LoreKeeper.Crawler.All.cs
#nullable enable
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Web;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LoreKeeper.Crawler;

// ===========================
// Config models
// ===========================
public sealed class CrawlerConfig
{
    public List<WikiSite> Wikis { get; set; } = new();
    public string SeedCategory { get; set; } = "Category:Characters";
    public bool DiscoverCategories { get; set; } = false; // discover all categories on each wiki
    public bool SkipUnchanged { get; set; } = true;       // if true, skip when rev-id matches storage
    public string ConnectionString { get; set; } = "Data Source=lorekeeper.db;Cache=Shared";
}

public sealed class WikiSite
{
    public string Host { get; set; } = default!;          // e.g. https://defiance-of-the-fall.fandom.com
    public List<string>? SeedCategories { get; set; }     // optional extra seeds per wiki
}

// ===========================
// Data contracts & interfaces
// ===========================
public sealed record WikiPage(
    string Title,
    string Url,
    string? RevId,
    string Html,
    IReadOnlyList<string> Categories
);

public interface IWikiClient
{
    IAsyncEnumerable<string> GetCategoryMembersAsync(string host, string category, CancellationToken ct);
    Task<WikiPage?> GetPageAsync(string host, string title, CancellationToken ct);
    Task<string?> GetLatestRevIdAsync(string host, string title, CancellationToken ct);
    Task<List<string>> GetAllCategoriesAsync(string host, CancellationToken ct);
}

public sealed class StoredPage
{
    public string Host { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string Url { get; set; } = default!;
    public string? RevId { get; set; }
    public string Html { get; set; } = default!;
    public IReadOnlyList<string>? Categories { get; set; }
    public DateTime FetchedUtc { get; set; }
}

public interface IStorage
{
    Task EnsureSchemaAsync(CancellationToken ct);
    Task<string?> GetSavedRevIdAsync(string host, string title, CancellationToken ct);
    Task UpsertPageAsync(StoredPage page, CancellationToken ct);
    Task<int> CleanupDuplicatesAsync(string host, CancellationToken ct);
}

// ===========================
// Background crawler service
// ===========================
public sealed class CrawlService(
    ILogger<CrawlService> logger,
    IOptions<CrawlerConfig> options,
    IWikiClient wiki,
    IStorage storage
) : BackgroundService
{
    private readonly ILogger<CrawlService> _logger = logger;
    private readonly CrawlerConfig _config = options.Value;
    private readonly IWikiClient _wiki = wiki;
    private readonly IStorage _storage = storage;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Config: Wikis={Count}, SeedCategory={Seed}, DiscoverCategories={Discover}, SkipUnchanged={Skip}",
            _config.Wikis.Count, _config.SeedCategory, _config.DiscoverCategories, _config.SkipUnchanged);

        await _storage.EnsureSchemaAsync(stoppingToken);

        foreach (var site in _config.Wikis)
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(site.Host)) continue;

            _logger.LogInformation("== Crawling {Host} ==", site.Host);

            // 1) Optional: discover all categories
            var categories = new List<string>();
            if (_config.DiscoverCategories)
            {
                try
                {
                    categories = await _wiki.GetAllCategoriesAsync(site.Host, stoppingToken);
                    _logger.LogInformation("Discovered {Count} categories on {Host}", categories.Count, site.Host);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to list categories on {Host}", site.Host);
                }
            }

            // Always include configured seeds
            if (!string.IsNullOrWhiteSpace(_config.SeedCategory))
                categories.Add(_config.SeedCategory);
            if (site.SeedCategories is { Count: > 0 })
                categories.AddRange(site.SeedCategories);

            categories = categories
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Per-run dedup for page titles
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var category in categories)
            {
                if (stoppingToken.IsCancellationRequested) break;

                _logger.LogInformation("Fetching category members: {Category}", category);

                await foreach (var title in _wiki.GetCategoryMembersAsync(site.Host, category, stoppingToken))
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    // 2) per-run de-dup
                    if (!seenTitles.Add(title))
                    {
                        _logger.LogDebug("Skip (seen this run): {Title}", title);
                        continue;
                    }

                    // 3) optional: skip unchanged by rev-id
                    if (_config.SkipUnchanged)
                    {
                        try
                        {
                            var remoteRev = await _wiki.GetLatestRevIdAsync(site.Host, title, stoppingToken);
                            if (!string.IsNullOrEmpty(remoteRev))
                            {
                                var localRev = await _storage.GetSavedRevIdAsync(site.Host, title, stoppingToken);
                                if (!string.IsNullOrEmpty(localRev) &&
                                    string.Equals(localRev, remoteRev, StringComparison.Ordinal))
                                {
                                    _logger.LogDebug("Skip (unchanged rev {Rev}): {Title}", remoteRev, title);
                                    continue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Rev-id check failed for {Title} on {Host}; continuing", title, site.Host);
                        }
                    }

                    // 4) fetch full content
                    WikiPage? page;
                    try
                    {
                        page = await _wiki.GetPageAsync(site.Host, title, stoppingToken);
                        if (page is null)
                        {
                            _logger.LogWarning("No page returned for {Title} on {Host}", title, site.Host);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch page {Title} on {Host}", title, site.Host);
                        continue;
                    }

                    // 5) persist (Upsert)
                    try
                    {
                        await _storage.UpsertPageAsync(new StoredPage
                        {
                            Host = site.Host,
                            Title = page.Title,
                            Url = page.Url,
                            RevId = page.RevId,
                            Html = page.Html,
                            Categories = page.Categories,
                            FetchedUtc = DateTime.UtcNow
                        }, stoppingToken);

                        _logger.LogInformation("Saved: {Title}", page.Title);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save page {Title} on {Host}", page.Title, site.Host);
                    }
                }
            }

            // 6) storage-level duplicate cleanup
            try
            {
                _logger.LogInformation("De-duplicating rows for {Host}…", site.Host);
                var removed = await _storage.CleanupDuplicatesAsync(site.Host, stoppingToken);
                if (removed > 0)
                    _logger.LogInformation("Removed {Count} duplicate row(s) for {Host}", removed, site.Host);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Duplicate cleanup failed for {Host}", site.Host);
            }
        }

        _logger.LogInformation("Done.");
    }
}

// ===========================
// MediaWiki client
// ===========================
public sealed class MediaWikiClient(HttpClient http, ILogger<MediaWikiClient> logger) : IWikiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _http = http;
    private readonly ILogger<MediaWikiClient> _logger = logger;

    public async IAsyncEnumerable<string> GetCategoryMembersAsync(string host, string category, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // action=query&list=categorymembers&cmtitle=Category:...&cmlimit=500&cmtype=page
        var baseUrl = $"{host.TrimEnd('/')}/api.php";
        string? cmContinue = null;

        do
        {
            var qs = new Dictionary<string, string?>
            {
                ["action"] = "query",
                ["format"] = "json",
                ["list"] = "categorymembers",
                ["cmtitle"] = category,
                ["cmlimit"] = "500",
                ["cmtype"] = "page"
            };
            if (!string.IsNullOrEmpty(cmContinue)) qs["cmcontinue"] = cmContinue;

            var reqUrl = $"{baseUrl}?{BuildQuery(qs)}";
            using var res = await _http.GetAsync(reqUrl, ct);
            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("query", out var query) &&
                query.TryGetProperty("categorymembers", out var members) &&
                members.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in members.EnumerateArray())
                {
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(title))
                        yield return title!;
                }
            }

            if (root.TryGetProperty("continue", out var cont) &&
                cont.TryGetProperty("cmcontinue", out var cmc))
            {
                cmContinue = cmc.GetString();
            }
            else cmContinue = null;

        } while (!string.IsNullOrEmpty(cmContinue) && !ct.IsCancellationRequested);
    }

    public async Task<WikiPage?> GetPageAsync(string host, string title, CancellationToken ct)
    {
        // Use action=parse to get HTML, revid, and categories
        // action=parse&page=Title&prop=text|revid|categories&format=json
        var baseUrl = $"{host.TrimEnd('/')}/api.php";
        var qs = new Dictionary<string, string?>
        {
            ["action"] = "parse",
            ["format"] = "json",
            ["page"] = title,
            ["prop"] = "text|revid|categories"
        };
        var reqUrl = $"{baseUrl}?{BuildQuery(qs)}";

        using var res = await _http.GetAsync(reqUrl, ct);
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;

        if (!root.TryGetProperty("parse", out var parse)) return null;

        string resolvedTitle = parse.TryGetProperty("title", out var pt) ? pt.GetString() ?? title : title;
        string urlTitle = resolvedTitle.Replace(' ', '_');
        var url = $"{host.TrimEnd('/')}/wiki/{Uri.EscapeDataString(urlTitle)}";

        string? revId = null;
        if (parse.TryGetProperty("revid", out var rid) && rid.ValueKind is JsonValueKind.Number)
            revId = rid.GetInt64().ToString();

        string html = "";
        if (parse.TryGetProperty("text", out var textObj) && textObj.TryGetProperty("*", out var textStar))
            html = textStar.GetString() ?? "";

        var cats = new List<string>();
        if (parse.TryGetProperty("categories", out var catArr) && catArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in catArr.EnumerateArray())
            {
                // c["*"] holds category name without "Category:"
                if (c.TryGetProperty("*", out var nameProp))
                {
                    var name = nameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        cats.Add("Category:" + name);
                }
            }
        }

        return new WikiPage(
            Title: resolvedTitle,
            Url: url,
            RevId: revId,
            Html: html,
            Categories: cats
        );
    }

    public async Task<string?> GetLatestRevIdAsync(string host, string title, CancellationToken ct)
    {
        // action=query&prop=revisions&rvprop=ids&titles=Title&format=json
        var baseUrl = $"{host.TrimEnd('/')}/api.php";
        var qs = new Dictionary<string, string?>
        {
            ["action"] = "query",
            ["format"] = "json",
            ["prop"] = "revisions",
            ["rvprop"] = "ids",
            ["rvslots"] = "main",
            ["titles"] = title
        };
        var reqUrl = $"{baseUrl}?{BuildQuery(qs)}";

        using var res = await _http.GetAsync(reqUrl, ct);
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;

        if (root.TryGetProperty("query", out var query) &&
            query.TryGetProperty("pages", out var pages) &&
            pages.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in pages.EnumerateObject())
            {
                var page = p.Value;
                if (page.TryGetProperty("revisions", out var revs) && revs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in revs.EnumerateArray())
                    {
                        if (r.TryGetProperty("revid", out var rid) && rid.ValueKind is JsonValueKind.Number)
                            return rid.GetInt64().ToString();
                    }
                }
            }
        }
        return null;
    }

    public async Task<List<string>> GetAllCategoriesAsync(string host, CancellationToken ct)
    {
        // action=query&list=allcategories&aclimit=500&accontinue=...
        var baseUrl = $"{host.TrimEnd('/')}/api.php";
        string? acContinue = null;
        var result = new List<string>();

        do
        {
            var qs = new Dictionary<string, string?>
            {
                ["action"] = "query",
                ["format"] = "json",
                ["list"] = "allcategories",
                ["aclimit"] = "500"
            };
            if (!string.IsNullOrEmpty(acContinue)) qs["accontinue"] = acContinue;

            var reqUrl = $"{baseUrl}?{BuildQuery(qs)}";
            using var res = await _http.GetAsync(reqUrl, ct);
            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("query", out var query) &&
                query.TryGetProperty("allcategories", out var cats) &&
                cats.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cats.EnumerateArray())
                {
                    if (c.TryGetProperty("*", out var nameProp))
                    {
                        var name = nameProp.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                            result.Add("Category:" + name);
                    }
                }
            }

            if (root.TryGetProperty("continue", out var cont) &&
                cont.TryGetProperty("accontinue", out var acc))
            {
                acContinue = acc.GetString();
            }
            else acContinue = null;

        } while (!string.IsNullOrEmpty(acContinue) && !ct.IsCancellationRequested);

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildQuery(IDictionary<string, string?> values)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in values)
        {
            if (kv.Value is null) continue;
            if (!first) sb.Append('&'); else first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
        }
        return sb.ToString();
    }
}

// ===========================
// SQLite storage (Dapper)
// ===========================
public sealed class SqliteStorage(
    IOptions<CrawlerConfig> options,
    ILogger<SqliteStorage> logger
) : IStorage, IAsyncDisposable
{
    private readonly string _connString = options.Value.ConnectionString;
    private readonly ILogger<SqliteStorage> _logger = logger;
    private SqliteConnection? _conn;

    private async Task<SqliteConnection> GetOpenAsync(CancellationToken ct)
    {
        if (_conn is { State: System.Data.ConnectionState.Open }) return _conn;
        _conn = new SqliteConnection(_connString);
        await _conn.OpenAsync(ct);
        return _conn;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        var conn = await GetOpenAsync(ct);

        var sql = """
        PRAGMA journal_mode = WAL;

        CREATE TABLE IF NOT EXISTS Pages (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Host TEXT NOT NULL,
            Title TEXT NOT NULL,
            Url TEXT NOT NULL,
            RevId TEXT NULL,
            Html TEXT NOT NULL,
            CategoriesJson TEXT NULL,
            FetchedUtc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Pages_Host_Title ON Pages(Host, Title);
        CREATE INDEX IF NOT EXISTS IX_Pages_Host_Title_Rev ON Pages(Host, Title, RevId);
        """;

        await conn.ExecuteAsync(sql);
    }

    public async Task<string?> GetSavedRevIdAsync(string host, string title, CancellationToken ct)
    {
        var conn = await GetOpenAsync(ct);
        var sql = """
        SELECT RevId
        FROM Pages
        WHERE Host = @host AND Title = @title
        ORDER BY Id DESC
        LIMIT 1;
        """;
        return await conn.ExecuteScalarAsync<string?>(sql, new { host, title });
    }

    public async Task UpsertPageAsync(StoredPage page, CancellationToken ct)
    {
        var conn = await GetOpenAsync(ct);

        // Strategy: allow multiple rows per (host,title) across different revs.
        // If the same (host,title,rev) exists, update the latest content.
        var sql = """
        INSERT INTO Pages (Host, Title, Url, RevId, Html, CategoriesJson, FetchedUtc)
        VALUES (@Host, @Title, @Url, @RevId, @Html, @CategoriesJson, @FetchedUtc);

        -- If you prefer strict upsert by (Host,Title,RevId), create UNIQUE index and use ON CONFLICT.
        """;

        var catsJson = page.Categories is null ? null : JsonSerializer.Serialize(page.Categories);
        await conn.ExecuteAsync(sql, new
        {
            page.Host,
            page.Title,
            page.Url,
            page.RevId,
            page.Html,
            CategoriesJson = catsJson,
            FetchedUtc = page.FetchedUtc.ToString("o")
        });
    }

    public async Task<int> CleanupDuplicatesAsync(string host, CancellationToken ct)
    {
        var conn = await GetOpenAsync(ct);

        // Remove duplicates keeping the newest row per (Host, Title, COALESCE(RevId,''))
        var sql = """
        WITH Ranked AS (
            SELECT
                Id,
                ROW_NUMBER() OVER (
                    PARTITION BY Host, Title, COALESCE(RevId, '')
                    ORDER BY FetchedUtc DESC, Id DESC
                ) AS rn
            FROM Pages
            WHERE Host = @host
        )
        DELETE FROM Pages
        WHERE Id IN (SELECT Id FROM Ranked WHERE rn > 1);
        """;

        return await conn.ExecuteAsync(sql, new { host });
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is not null)
        {
            await _conn.DisposeAsync();
            _conn = null;
        }
    }
}

// ===========================
// DI extension
// ===========================
public static class CrawlerServiceCollectionExtensions
{
    public static IServiceCollection AddCrawlerServices(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<CrawlerConfig>(config.GetSection("Crawler"));

        // HttpClient for MediaWiki
        services.AddHttpClient<IWikiClient, MediaWikiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LoreKeeper.Crawler/1.0");
        });

        services.AddSingleton<IStorage, SqliteStorage>();
        services.AddHostedService<CrawlService>();

        return services;
    }
}
```

---

### How to wire it up (Program.cs)

If your `Program.cs` isn’t already set, here’s the minimal setup that reads `appsettings.json`, binds the `Crawler` section, and registers the crawler:

```csharp
using LoreKeeper.Crawler;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddCrawlerServices(builder.Configuration);

var app = builder.Build();
app.Run();
```

---

### `appsettings.json` example

```json
{
  "Crawler": {
    "ConnectionString": "Data Source=lorekeeper.db;Cache=Shared",
    "SeedCategory": "Category:Characters",
    "DiscoverCategories": false,
    "SkipUnchanged": true,
    "Wikis": [
      { "Host": "https://defiance-of-the-fall.fandom.com" },
      { "Host": "https://martial-world.fandom.com" },
      { "Host": "https://true-mw.fandom.com" }
    ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "System.Net.Http.HttpClient": "Information"
    }
  }
}
```

---

### Notes

* No reliance on `.Size` or other tuple members — the earlier compile error is gone.
* The MediaWiki client uses `action=parse` for HTML + categories and `action=query` for rev-ids and category listing.
* Duplicate cleanup is run **per host** at the end of each host crawl.
* The SQLite storage creates the schema automatically on first run (`EnsureSchemaAsync`).
* If you prefer a strict upsert by `(Host, Title, RevId)`, create a unique index and switch the insert to an `INSERT ... ON CONFLICT DO UPDATE` (you had that earlier — easy to swap in).

If you want me to split this back into separate files (client, storage, service) I can paste those too.
