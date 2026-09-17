using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Patchouli.Core.Diagnostics;

public enum HostActivityKind
{
    Startup,
    UiCommand,
    Mcp,
    Ocr,
    Scanning,
    Import,
    Sync,
    Indexing
}

public sealed record HostActivityItem(
    string Id,
    string Name,
    HostActivityKind Kind,
    DateTimeOffset StartedAt,
    string? Detail = null,
    bool IsPaused = false,
    bool IsWaitingRetry = false,
    string? PauseReason = null,
    string? RetryReason = null);

public sealed record HostActivitySnapshot(
    bool IsBusy,
    bool IsSleeping,
    string? SleepReason,
    string? ActiveSummary,
    IReadOnlyList<HostActivityItem> Items)
{
    public static HostActivitySnapshot Idle { get; } = new(false, true, null, null, Array.Empty<HostActivityItem>());
}

public interface IActivityScope : IDisposable
{
    string Id { get; }
    void UpdateDetail(string detail);
    void SetPaused(bool paused, string? reason = null);
    void SetWaitingRetry(bool waiting, string? reason = null);
}

public interface IHostActivityTracker
{
    HostActivitySnapshot Current { get; }
    event EventHandler<HostActivitySnapshot>? Changed;
    IObservable<HostActivitySnapshot> SnapshotStream { get; }

    IActivityScope BeginScope(string name, HostActivityKind kind, string? initialDetail = null,
        string? correlationId = null);

    Task FlushAsync(CancellationToken cancellationToken = default);
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
}
