using System.Collections.Concurrent;

namespace Patchouli.Infrastructure.Database;

/// <summary>
/// Serializes long-running database workflows for one runtime database within this process.
/// </summary>
internal static class SqliteDatabaseExecutionGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public static async Task<IDisposable> EnterAsync(string databasePath, CancellationToken cancellationToken)
    {
        string normalizedPath = Path.GetFullPath(databasePath);
        SemaphoreSlim gate = Gates.GetOrAdd(normalizedPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _disposed;

        public Lease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _gate.Release();
            }
        }
    }
}
