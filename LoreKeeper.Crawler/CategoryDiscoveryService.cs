using System.Text.Json;

namespace LoreKeeper.Crawler;

public sealed class CategoryDiscoveryService : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly CrawlerConfig _config;

    public CategoryDiscoveryService(IHttpClientFactory httpFactory, CrawlerConfig config)
    {
        _httpFactory = httpFactory;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // If disabled, no-op (keeps service registration simple)
        if (!_config.EnableCategoryDiscovery)
            return;

        var http = _httpFactory.CreateClient("wiki");
        var dataDir = !string.IsNullOrWhiteSpace(_config.DataDirectory) && Path.IsPathRooted(_config.DataDirectory)
            ? _config.DataDirectory
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data"));

        Directory.CreateDirectory(dataDir);

        // Accumulate per-wiki category sets and site names to compute global summaries at the end
        var perWikiCategorySets = new List<HashSet<string>>();
        var siteNames = new List<string>();

        foreach (var baseUrl in _config.Wikis)
        {
            if (stoppingToken.IsCancellationRequested) break;

            Console.WriteLine($"== Discovering categories for {baseUrl} ==");
            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? accontinue = null;

            do
            {
                var url = $"{baseUrl.TrimEnd('/')}/api.php?action=query&format=json&list=allcategories&aclimit=500" +
                          (accontinue is null ? "" : $"&accontinue={Uri.EscapeDataString(accontinue)}");

                using var resp = await http.GetAsync(url, stoppingToken);
                resp.EnsureSuccessStatusCode();

                using var stream = await resp.Content.ReadAsStreamAsync(stoppingToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: stoppingToken);

                var root = doc.RootElement;

                if (root.TryGetProperty("query", out var query) &&
                    query.TryGetProperty("allcategories", out var ac) &&
                    ac.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in ac.EnumerateArray())
                    {
                        // MediaWiki returns { "*": "CategoryName" } for each item
                        if (item.TryGetProperty("*", out var nameEl))
                        {
                            var name = nameEl.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                                categories.Add("Category:" + name);
                        }
                    }
                }

                accontinue = null;
                if (root.TryGetProperty("continue", out var cont) &&
                    cont.TryGetProperty("accontinue", out var acc))
                {
                    accontinue = acc.GetString();
                }

                // Small politeness delay between calls
                await Task.Delay(_config.DelayMsBetweenCalls, stoppingToken);
            }
            while (!string.IsNullOrEmpty(accontinue) && !stoppingToken.IsCancellationRequested);

            // Persist categories to disk, per-wiki (site-only file remains full set)
            var safeHostName = baseUrl
                .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                .Replace('/', '_');

            var outPath = Path.Combine(dataDir, $"categories-{safeHostName}.json");

            await File.WriteAllTextAsync(outPath,
                JsonSerializer.Serialize(categories.OrderBy(x => x).ToArray(), new JsonSerializerOptions { WriteIndented = true }),
                stoppingToken);

            Console.WriteLine($"Discovered {categories.Count} categories for {baseUrl}. Saved to {outPath}");

            // Keep for global summaries
            perWikiCategorySets.Add(categories);
            siteNames.Add(baseUrl);
        }

        // Global summaries: intersection, union, optional "at least K", and coverage map
        if (perWikiCategorySets.Count > 0)
        {
            // Intersection (in all sites)
            var intersection = new HashSet<string>(perWikiCategorySets[0], StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < perWikiCategorySets.Count; i++)
                intersection.IntersectWith(perWikiCategorySets[i]);

            // Union (in any site)
            var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in perWikiCategorySets) union.UnionWith(set);

            // Coverage map: category -> { count, sites[] }
            var coverage = new Dictionary<string, (int Count, List<string> Sites)>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < perWikiCategorySets.Count; i++)
            {
                var site = siteNames[i];
                foreach (var cat in perWikiCategorySets[i])
                {
                    if (!coverage.TryGetValue(cat, out var entry))
                        coverage[cat] = (1, new List<string> { site });
                    else
                    {
                        entry.Count += 1;
                        entry.Sites.Add(site);
                        coverage[cat] = entry;
                    }
                }
            }

            // Write categories-global (intersection)
            await WriteIfChangedAsync(Path.Combine(dataDir, "categories-global.json"),
                JsonSerializer.Serialize(intersection.OrderBy(x => x).ToArray(), new JsonSerializerOptions { WriteIndented = true }),
                stoppingToken);

            // Write categories-union (union)
            await WriteIfChangedAsync(Path.Combine(dataDir, "categories-union.json"),
                JsonSerializer.Serialize(union.OrderBy(x => x).ToArray(), new JsonSerializerOptions { WriteIndented = true }),
                stoppingToken);

            // Optional: categories-global-atleast-{k}.json (present in at least K sites)
            if (_config.MinSitesForGlobal is int k && k > 0)
            {
                var atleastK = coverage.Where(kv => kv.Value.Count >= k).Select(kv => kv.Key)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                await WriteIfChangedAsync(Path.Combine(dataDir, $"categories-global-atleast-{k}.json"),
                    JsonSerializer.Serialize(atleastK, new JsonSerializerOptions { WriteIndented = true }),
                    stoppingToken);
            }

            // Write coverage map
            var coverageDto = coverage
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => new { count = kv.Value.Count, sites = kv.Value.Sites });

            await WriteIfChangedAsync(Path.Combine(dataDir, "categories-coverage.json"),
                JsonSerializer.Serialize(coverageDto, new JsonSerializerOptions { WriteIndented = true }),
                stoppingToken);

            Console.WriteLine($"Global summaries written: intersection={intersection.Count}, union={union.Count}, coverage={coverage.Count}");
        }

        Console.WriteLine("Category discovery complete.");
    }

    private static async Task WriteIfChangedAsync(string path, string newContent, CancellationToken ct)
    {
        var shouldWrite = true;
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, ct);
            shouldWrite = !string.Equals(existing, newContent, StringComparison.Ordinal);
        }
        if (shouldWrite)
        {
            await File.WriteAllTextAsync(path, newContent, ct);
            Console.WriteLine($"Updated: {path}");
        }
        else
        {
            Console.WriteLine($"Unchanged: {path}");
        }
    }
}