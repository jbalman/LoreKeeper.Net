using System.Text.Json;
using LoreKeeper.Storage.Models;

namespace LoreKeeper.Crawler;

public sealed class CrawlService : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly CrawlerConfig _config;
    private readonly IDbBootstrapper _bootstrapper;
    private readonly IPagesRepository _repo;

    public CrawlService(IHttpClientFactory httpFactory, CrawlerConfig config, IDbBootstrapper bootstrapper,
        IPagesRepository repo)
    {
        _httpFactory = httpFactory;
        _config = config;
        _bootstrapper = bootstrapper;
        _repo = repo;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.EnableCrawler)
        {
            Console.WriteLine("Crawler disabled by configuration (Crawler.EnableCrawler = false).");
            return;
        }

        await _bootstrapper.InitializeAsync(stoppingToken);


        var http = _httpFactory.CreateClient("wiki");

        foreach (var baseUrl in _config.Wikis)
        {
            Console.WriteLine($"== Crawling {baseUrl} ==");
            // Process each configured category independently
            var categories = (_config.SeedCategories?.Length > 0)
                ? _config.SeedCategories
                : Array.Empty<string>();

            foreach (var category in categories)
            {
                if (string.IsNullOrWhiteSpace(category)) continue;

                Console.WriteLine($" -- Category: {category}");
                string? cmcontinue = null;

                do
                {
                    var listUrl =
                        $"{baseUrl}/api.php?action=query&format=json&formatversion=2&list=categorymembers&cmtitle={Uri.EscapeDataString(category)}&cmlimit=100" +
                        (cmcontinue is null ? "" : $"&cmcontinue={Uri.EscapeDataString(cmcontinue)}");

                    using var listResp = await http.GetAsync(listUrl, stoppingToken);
                    listResp.EnsureSuccessStatusCode();
                    using var listStream = await listResp.Content.ReadAsStreamAsync(stoppingToken);
                    using var listJson = await JsonDocument.ParseAsync(listStream, cancellationToken: stoppingToken);

                    if (listJson.RootElement.TryGetProperty("query", out var query) &&
                        query.TryGetProperty("categorymembers", out var members))
                    {
                        foreach (var m in members.EnumerateArray())
                        {
                            stoppingToken.ThrowIfCancellationRequested();

                            var pageId = m.GetProperty("pageid").GetInt32();
                            var title = m.GetProperty("title").GetString() ?? $"page-{pageId}";

                            var parseUrl =
                                $"{baseUrl}/api.php?action=parse&pageid={pageId}&prop=text|sections|links|categories&format=json&formatversion=2";
                            using var parseResp = await http.GetAsync(parseUrl, stoppingToken);
                            if (!parseResp.IsSuccessStatusCode)
                            {
                                Console.WriteLine($"! parse error {parseResp.StatusCode} for {title}");
                                continue;
                            }

                            var raw = await parseResp.Content.ReadAsStringAsync(stoppingToken);
                            using var parsed = JsonDocument.Parse(raw);
                            var now = DateTimeOffset.UtcNow;

                            string html = "";
                            int? revId = null;

                            if (parsed.RootElement.TryGetProperty("parse", out var parse) &&
                                parse.ValueKind == JsonValueKind.Object)
                            {
                                if (parse.TryGetProperty("text", out var text))
                                {
                                    if (text.ValueKind == JsonValueKind.String)
                                    {
                                        // formatversion=2: text is a plain HTML string
                                        html = text.GetString() ?? "";
                                    }
                                    else if (text.ValueKind == JsonValueKind.Object &&
                                             text.TryGetProperty("*", out var htmlEl))
                                    {
                                        // Back-compat for servers ignoring formatversion=2: text has a "*" property
                                        html = htmlEl.GetString() ?? "";
                                    }
                                }

                                if (parse.TryGetProperty("revid", out var revEl) &&
                                    revEl.ValueKind == JsonValueKind.Number)
                                {
                                    revId = revEl.GetInt32();
                                }
                            }

                            await _repo.UpsertPageAsync(new PageRow
                            {
                                Wiki = baseUrl,
                                PageId = pageId,
                                Title = title,
                                RevId = revId,
                                LastFetchedUtc = now
                            });

                            await _repo.UpsertBodyAsync(new PageBodyRow
                            {
                                Wiki = baseUrl,
                                PageId = pageId,
                                Format = "html",
                                Body = html,
                                FetchedUtc = now
                            });

                            Console.WriteLine($" - {title} ({pageId}) saved.");
                            await Task.Delay(_config.DelayMsBetweenCalls, stoppingToken);
                        }
                    }

                    cmcontinue = null;
                    if (listJson.RootElement.TryGetProperty("continue", out var cont) &&
                        cont.TryGetProperty("cmcontinue", out var cme))
                    {
                        cmcontinue = cme.GetString();
                    }

                    await Task.Delay(300, stoppingToken);
                } while (!string.IsNullOrEmpty(cmcontinue));
            }
        }

        Console.WriteLine("Done.");
    }
}