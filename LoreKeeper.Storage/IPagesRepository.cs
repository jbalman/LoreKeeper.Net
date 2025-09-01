using LoreKeeper.Storage.Models;

namespace LoreKeeper.Storage;

public interface IPagesRepository
{
    Task UpsertPageAsync(PageRow row);
    Task UpsertBodyAsync(PageBodyRow row);
}