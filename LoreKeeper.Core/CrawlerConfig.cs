namespace LoreKeeper.Core;

public sealed class CrawlerConfig
{
    public string[] Wikis { get; init; } = Array.Empty<string>();
    public string[] SeedCategories { get; init; } = Array.Empty<string>();
    public int DelayMsBetweenCalls { get; init; } = 250;
    public bool EnableCategoryDiscovery { get; init; } = false;
    public bool EnableCrawler { get; init; } = true;
    public string? DataDirectory { get; init; }
    public int? MinSitesForGlobal { get; init; } = null;

}