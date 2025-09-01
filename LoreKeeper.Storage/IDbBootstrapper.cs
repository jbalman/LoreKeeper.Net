namespace LoreKeeper.Storage;

public interface IDbBootstrapper
{
    Task InitializeAsync(CancellationToken ct);
}