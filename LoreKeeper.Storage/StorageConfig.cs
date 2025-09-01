namespace LoreKeeper.Storage;

public sealed class StorageConfig
{
    public string ConnectionString { get; init; } = "Data Source=./data/lorekeeper.db";
}