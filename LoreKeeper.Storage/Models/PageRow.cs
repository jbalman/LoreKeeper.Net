namespace LoreKeeper.Storage.Models;

public sealed record PageRow
{
    public string Wiki { get; init; } = "";
    public int PageId { get; init; }
    public string Title { get; init; } = "";
    public int? RevId { get; init; }
    public DateTimeOffset LastFetchedUtc { get; init; }
}
