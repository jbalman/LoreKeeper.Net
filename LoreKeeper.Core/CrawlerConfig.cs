namespace LoreKeeper.Core;

public sealed class CrawlerConfig
{
    public string[] Wikis { get; init; } = Array.Empty<string>();
    public string   SeedCategory { get; init; } = "Category:Main";
    public int      DelayMsBetweenCalls { get; init; } = 250;
}