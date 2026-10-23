using System.Data.Common;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

/// <summary>
/// Runs the real schema migration once per instance and keeps the migrated file as an immutable
/// template. Every consumer receives an independent file copy behind its own path and connection
/// pools, so a case that only needs "a migrated database" pays a file copy instead of a full
/// migration. The template lives only inside this process — there is deliberately no cross-run
/// on-disk cache — and it carries schema objects plus the <c>schema_migrations</c> bookkeeping
/// only: no library, business IDs, or credentials are ever written to it.
/// </summary>
internal sealed class MigratedDatabaseTemplate : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<SqliteConnectionFactory, CancellationToken, Task> _migrate;
    private Task<string>? _initialization;
    private string? _templatePath;

    /// <summary>
    /// Creates a template that runs the real <see cref="MigrationRunner"/> over
    /// <see cref="TestPaths.MigrationsDirectory"/> on first use. The optional
    /// <paramref name="migrate"/> step exists so focused tests can inject a failing or counting
    /// migration without touching <see cref="Shared"/>.
    /// </summary>
    internal MigratedDatabaseTemplate(
        Func<SqliteConnectionFactory, CancellationToken, Task>? migrate = null)
    {
        _migrate = migrate ?? RunRealMigrationsAsync;
    }

    /// <summary>The process-wide template used by <see cref="TemporarySqliteDatabase.CreateMigratedAsync"/>.</summary>
    public static MigratedDatabaseTemplate Shared { get; } = CreateShared();

    /// <summary>
    /// Returns the path of the ready template, running the migration on first use. Concurrent
    /// callers share one migration; a failed or cancelled attempt removes its partial file, is
    /// reported to every waiter, and lets the next caller retry on a fresh path.
    /// </summary>
    public async Task<string> EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        Task<string>? current = Volatile.Read(ref _initialization);
        if (current is { IsCompletedSuccessfully: true })
        {
            return current.Result;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            current = _initialization;
            if (current is null || current.IsCanceled || current.IsFaulted)
            {
                current = InitializeCoreAsync(cancellationToken);
                Volatile.Write(ref _initialization, current);
            }
        }
        finally
        {
            _gate.Release();
        }

        return await current;
    }

    /// <summary>
    /// Copies the ready template to <paramref name="destinationPath"/>, which must not exist. The
    /// copy gets writable attributes and is owned entirely by the caller.
    /// </summary>
    public async Task CreateCopyAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        string templatePath = await EnsureInitializedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        bool created = false;
        try
        {
            File.Copy(templatePath, destinationPath, false);
            created = true;
            // File.Copy keeps the template's read-only attribute; each copy must be writable.
            File.SetAttributes(destinationPath, FileAttributes.Normal);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            // Only a destination this call actually created may be deleted — a pre-existing file
            // that made File.Copy fail belongs to someone else.
            if (created)
            {
                DeleteDatabaseFiles(destinationPath);
            }

            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        string? path = _templatePath;
        if (path is not null)
        {
            try
            {
                DeleteDatabaseFiles(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"MigratedDatabaseTemplate failed to delete '{path}' and its SQLite sidecars; " +
                    "dispose every connection using the template before the fixture.", ex);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static MigratedDatabaseTemplate CreateShared()
    {
        MigratedDatabaseTemplate template = new();
        // The shared template lives as long as the testhost. Deleting it on process exit keeps a
        // finished run from leaving a new orphan database under .tmp/tests.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => template.DeleteAtProcessExit();
        return template;
    }

    private static Task RunRealMigrationsAsync(SqliteConnectionFactory factory,
        CancellationToken cancellationToken)
    {
        return new MigrationRunner(factory, TestPaths.MigrationsDirectory).RunAsync(cancellationToken);
    }

    private async Task<string> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"patchouli-migrated-template-{Guid.NewGuid():N}.sqlite");
        SqliteConnectionFactory factory = new(path);
        try
        {
            await _migrate(factory, cancellationToken);
            await CheckpointAndVerifyNoUnmergedWalAsync(factory, cancellationToken);
            // Only non-pooled admin connections have touched the file; this also releases any raw
            // "Data Source=..." pool a migration step may have opened before the file is copied.
            SqliteTestCleanup.ReleasePools(path);
            // The template is read-only so no consumer can ever write through the shared file.
            File.SetAttributes(path, FileAttributes.ReadOnly);
            _templatePath = path;
            return path;
        }
        catch
        {
            try
            {
                DeleteDatabaseFiles(path);
            }
            catch (Exception cleanupFailure)
            {
                // Keep the original failure; report the cleanup problem instead of replacing it.
                Console.Error.WriteLine(
                    $"MigratedDatabaseTemplate failed to remove partial template '{path}': {cleanupFailure}");
            }

            throw;
        }
    }

    /// <summary>
    /// Merges every WAL frame into the main file and truncates the log, then verifies nothing
    /// unmerged is left behind. Copies are made of the main file only, so frames still sitting in
    /// the WAL would silently be missing from every copy.
    /// </summary>
    private static async Task CheckpointAndVerifyNoUnmergedWalAsync(SqliteConnectionFactory factory,
        CancellationToken cancellationToken)
    {
        await using (SqliteConnection connection = factory.CreateAdminConnection())
        {
            await connection.OpenAsync(cancellationToken);
            long busy;
            long log;
            long checkpointed;
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "pragma wal_checkpoint(TRUNCATE);";
                await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                busy = reader.GetInt64(0);
                log = reader.GetInt64(1);
                checkpointed = reader.GetInt64(2);
            }

            if (busy != 0 || log != checkpointed)
            {
                throw new InvalidOperationException(
                    $"Migrated database template '{factory.DatabasePath}' could not merge its WAL " +
                    $"(busy={busy}, wal frames={log}, checkpointed={checkpointed}).");
            }
        }

        // Closing the last connection removes a fully checkpointed -wal/-shm pair. A non-empty
        // -wal at this point means the copy path would lose unmerged frames.
        string walPath = $"{factory.DatabasePath}-wal";
        if (File.Exists(walPath) && new FileInfo(walPath).Length > 0)
        {
            throw new InvalidOperationException(
                $"Migrated database template '{factory.DatabasePath}' still has unmerged WAL frames at '{walPath}'.");
        }
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        ClearReadOnlyAttribute(path);
        SqliteTestCleanup.ReleasePools(path);
        new SqliteConnectionFactory(path).DeleteDatabaseFiles();
    }

    private void DeleteAtProcessExit()
    {
        string? path = _templatePath;
        if (path is null)
        {
            return;
        }

        try
        {
            DeleteDatabaseFiles(path);
        }
        catch (Exception exception)
        {
            // Best-effort teardown only: a leftover file under .tmp/tests is preferable to throwing
            // inside ProcessExit and failing the whole testhost at the end of a run.
            Console.Error.WriteLine(
                $"MigratedDatabaseTemplate exit cleanup failed for '{path}': {exception}");
        }
    }
}
