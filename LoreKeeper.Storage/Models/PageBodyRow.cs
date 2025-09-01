namespace LoreKeeper.Storage.Models;

public sealed record PageBodyRow
{
    public string Wiki { get; init; } = "";
    public int PageId { get; init; }
    public string Format { get; init; } = "html";
    public string Body { get; init; } = "";
    public DateTimeOffset FetchedUtc { get; init; }
}
