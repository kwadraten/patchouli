namespace LiteratureApp.Infrastructure.Database;

public sealed record DatabaseOptions
{
    public required string RuntimeDatabasePath { get; init; }
    public required string AppDataRoot { get; init; }
    public required string CacheRoot { get; init; }
    public required string SnapshotRoot { get; init; }
}
