namespace LoreKeeper.Storage;

public sealed class StorageConfig
{
    public string ConnectionString { get; init; } = "Data Source=./data/lorekeeper.db";

    // Startup data reset flags (mutually exclusive)
    public bool DropOnStart { get; init; } = false;
    public bool TruncateOnStart { get; init; } = false;

}