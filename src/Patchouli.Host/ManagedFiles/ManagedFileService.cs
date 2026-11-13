using Patchouli.Core.Downloads;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Patchouli.Core.Results;
using Patchouli.Host.Downloads;

namespace Patchouli.Host.ManagedFiles;

/// <summary>
/// One declared managed file normalized across owners (FSX extensions and plugins).
/// <see cref="DestinationPath"/> is the canonical local path; it is internal host state and
/// is never serialized into MCP payloads.
/// </summary>
public sealed record ManagedFileDownload(
    string RelativePath,
    string? DownloadUrl,
    string DestinationPath,
    long? ExpectedBytes = null,
    string? Sha256 = null,
    string? Version = null)
{
    /// <summary>
    /// True when the destination existed at plan time but failed declared verification (size or
    /// pinned digest). A confirmed plan explicitly authorizes replacing that mismatched file;
    /// without the flag the downloader refuses to touch it.
    /// </summary>
    public bool OverwritesExisting { get; init; }

    /// <summary>
    /// Byte length of the mismatched file observed at plan time. Revalidated immediately before
    /// the confirmed replacement installs — a newer user file written between the prompt and the
    /// download completing is never clobbered.
    /// </summary>
    public long? ExistingLength { get; init; }

    /// <summary>UTC write ticks paired with <see cref="ExistingLength"/> for the revalidation check.</summary>
    public long? ExistingWriteUtcTicks { get; init; }

    /// <summary>
    /// SHA-256 of the mismatched file's actual bytes at plan time. Length+timestamp cannot spot
    /// a same-size rewrite that preserved the timestamp — the digest catches it. Null when the
    /// file could not be hashed at plan time (identity then degrades to length+ticks).
    /// </summary>
    public string? ExistingSha256 { get; init; }
}

/// <summary>
/// One resolved declaration input for <see cref="ManagedFileService.PlanAsync"/>: the owner
/// already computed the canonical destination; the engine owns verification state.
/// </summary>
public sealed record ManagedFileTarget(
    string RelativePath,
    string? DownloadUrl,
    string DestinationPath,
    long? ExpectedBytes = null,
    string? Sha256 = null,
    string? Version = null);

/// <summary>
/// The missing/invalid managed files for one owner. <see cref="OwnerKey"/> namespaces the
/// owner (<c>extension:{id}[scope]</c>, <c>plugin:{id}</c>, <c>inventory:{directory}</c>) so
/// single-flight identity can never merge across owners.
/// </summary>
public sealed record ManagedFilesPlan(
    string OwnerKey,
    string DisplayName,
    IReadOnlyList<ManagedFileDownload> Files)
{
    public bool IsEmpty => Files.Count == 0;

    /// <summary>Total size across files whose declarations pin a byte count.</summary>
    public long TotalExpectedBytes => Files.Sum(file => file.ExpectedBytes ?? 0);

    /// <summary>
    /// The complete declaration set for this plan — every declared file whether it is already
    /// satisfied or missing. Single-flight identity is derived from this immutable set
    /// (URL/target/bytes/digest), so concurrent ensures join by what is required rather than by
    /// the mutable missing subset. Empty for hand-built plans; the identity then uses
    /// <see cref="Files"/>.
    /// </summary>
    public IReadOnlyList<ManagedFileDownload> RequiredFiles { get; init; } = [];
}

/// <summary>
/// The single host implementation of managed-file verification, confirmed transfer,
/// per-destination locking, single-flight ensure and lifecycle draining. The FSX extension
/// facade (<see cref="Extensions.ExtensionManagedFileService"/>) and the plugin facade
/// (<see cref="Plugins.PluginManagedFileService"/>) keep their public contracts and own
/// declaration→destination resolution; everything below the plan is this one engine.
/// Transport is used only after confirmation — planning, status and ensure prechecks never
/// touch the network or create directories.
/// </summary>
public sealed class ManagedFileService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpMessageInvoker _http;
    private readonly bool _ownsHttp;

    // One in-flight ensure per immutable required-declaration set: a second caller for the same
    // file set awaits the same confirmation+download instead of stacking prompts.
    private readonly ConcurrentDictionary<string, EnsureFlight> _ensures =
        new(StringComparer.Ordinal);

    // One lifecycle gate for admission, shutdown and drain: a caller either registers its
    // operation slot or is rejected BEFORE any work starts — shutdown can never observe an
    // empty drain while a new operation is being admitted. _drained is the single pending
    // shutdown task every DisposeAsync caller awaits.
    private readonly object _lifecycle = new();

    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private int _active;
    private bool _closed;
    private bool _shutdownStarted;

    private readonly TaskCompletionSource _shutdownCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManagedFileService(HttpMessageInvoker? http = null)
    {
        // Captured once: the struct stays valid after the source is disposed, so an operation
        // racing teardown can still link its token without touching a disposed source.
        _shutdownToken = _shutdown.Token;
        _ownsHttp = http is null;
        _http = http ?? new HttpMessageInvoker(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        }, true);
    }

    /// <summary>
    /// The actually missing/invalid declared files for one resolved declaration set. Destinations
    /// are deduplicated under the platform's file-name comparison; each file's length and digest
    /// are read once. Never creates directories or touches the network.
    /// </summary>
    public async Task<ManagedFilesPlan> PlanAsync(string ownerKey, string displayName,
        IEnumerable<ManagedFileTarget> targets, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        ArgumentNullException.ThrowIfNull(targets);
        List<ManagedFileDownload> required = [];
        List<ManagedFileDownload> downloads = [];
        HashSet<string> destinations = new(PathComparer());
        foreach (ManagedFileTarget target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!destinations.Add(target.DestinationPath))
            {
                continue;
            }

            (ManagedFileDownload download, bool satisfied) =
                await DescribeExistingAsync(target, cancellationToken).ConfigureAwait(false);
            required.Add(download);
            if (!satisfied)
            {
                downloads.Add(download);
            }
        }

        return new ManagedFilesPlan(ownerKey, displayName, downloads.AsReadOnly())
        {
            RequiredFiles = required.AsReadOnly()
        };
    }

    /// <summary>
    /// Ensures the owner's declared files exist, asking the optional interaction once per
    /// required set. <paramref name="planFactory"/> replans the same captured declaration/config
    /// inputs — before the prompt and again after a reported success — so a stale snapshot never
    /// authorizes anything. An empty plan succeeds; missing files without an interaction report
    /// an actionable resource-missing failure — no caller ever triggers an unconfirmed download.
    /// Concurrent ensures for the same required set share one confirmation+download; each
    /// caller's cancellation only withdraws that caller, and the shared operation is cancelled
    /// once no waiters remain.
    /// </summary>
    public async Task<Result> EnsureAsync(string ownerKey,
        Func<CancellationToken, Task<ManagedFilesPlan>> planFactory,
        Func<ManagedFilesPlan, Func<IProgress<double>?, CancellationToken, Task<Result>>,
            CancellationToken, Task<Result>>? interaction,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(planFactory);
        ManagedFilesPlan plan = await planFactory(cancellationToken).ConfigureAwait(false);
        if (plan.IsEmpty)
        {
            return Result.Success();
        }

        string key = FlightKey(ownerKey, plan);
        while (true)
        {
            EnsureFlight flight = JoinFlight(key, planFactory, interaction);
            try
            {
                // The shared operation materializes only now — after this waiter's registration —
                // so a flight nobody observes never starts and a completed flight simply yields
                // its result. Each waiter cancels only its own wait; the shared operation lives
                // on the flight's own shutdown-linked token.
                return await flight.GetOrStartOperation()
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The shared operation was cancelled without this caller asking. A flight that
                // was orphaned (its last other waiter withdrew) is retried on a fresh one; a
                // shared user/modal cancellation — the operation ended cancelled with no orphan
                // cancel-requested — is a real cancellation for every waiter and propagates
                // rather than reopening the prompt. Shutdown rethrows as disposed.
                ThrowIfClosed();
                if (!flight.CancelRequested)
                {
                    throw;
                }
            }
            finally
            {
                Task<Result>? draining = flight.Withdraw(out bool wasLast);
                if (wasLast)
                {
                    // No shared owner remains: request cancellation and drain the real work
                    // before this caller's cancellation is visible upstream.
                    if (draining is not null && !draining.IsCompleted)
                    {
                        flight.RequestCancel();
                        await ((Task)draining).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }

                    _ensures.TryRemove(new KeyValuePair<string, EnsureFlight>(key, flight));
                    flight.DisposeCancellation();
                }
                else if (flight.IsDead)
                {
                    _ensures.TryRemove(new KeyValuePair<string, EnsureFlight>(key, flight));
                }
            }
        }
    }

    private EnsureFlight JoinFlight(string key,
        Func<CancellationToken, Task<ManagedFilesPlan>> planFactory,
        Func<ManagedFilesPlan, Func<IProgress<double>?, CancellationToken, Task<Result>>,
            CancellationToken, Task<Result>>? interaction)
    {
        while (true)
        {
            // The flight token is also linked to shutdown inside BeginOperation, which registers
            // the operation slot under the lifecycle gate before any work can start.
            EnsureFlight flight = GetOrCreateFlight(key,
                token => BeginOperation(inner => RunEnsureAsync(planFactory, interaction, inner),
                    token));
            if (flight.TryJoin())
            {
                return flight;
            }

            // Lost the race against its cancel-request: it is dead, back out (no waiter was
            // registered) and retry for a fresh one.
            _ensures.TryRemove(new KeyValuePair<string, EnsureFlight>(key, flight));
            ThrowIfClosed();
        }
    }

    /// <summary>
    /// Returns a live flight for the key — never a completed or cancel-requested one. Dead
    /// flights are evicted and replaced so each ensure re-verifies the plan inside the
    /// operation; a synchronously completing operation is a legitimate result the joined caller
    /// awaits, never a reason to spin.
    /// </summary>
    private EnsureFlight GetOrCreateFlight(string key,
        Func<CancellationToken, Task<Result>> run)
    {
        while (true)
        {
            EnsureFlight flight = _ensures.GetOrAdd(key,
                _ => new EnsureFlight(run, _shutdownToken));
            if (!flight.IsDead)
            {
                return flight;
            }

            _ensures.TryRemove(new KeyValuePair<string, EnsureFlight>(key, flight));
            // Every fresh flight still dies instantly only while shutdown cancels them all —
            // without this check a closing service would spin here forever.
            ThrowIfClosed();
        }
    }

    private async Task<Result> RunEnsureAsync(
        Func<CancellationToken, Task<ManagedFilesPlan>> planFactory,
        Func<ManagedFilesPlan, Func<IProgress<double>?, CancellationToken, Task<Result>>,
            CancellationToken, Task<Result>>? interaction,
        CancellationToken cancellationToken)
    {
        // The flight key is the immutable required set, not the missing subset — a caller whose
        // plan predates a shared operation installing files can still arrive here. Re-plan the
        // same frozen inputs so an already-satisfied set never prompts or downloads again.
        ManagedFilesPlan plan = await planFactory(cancellationToken).ConfigureAwait(false);
        if (plan.IsEmpty)
        {
            return Result.Success();
        }

        if (interaction is null)
        {
            string sample = plan.Files[0].RelativePath;
            return Result.Failure(AppErrorCodes.NotFound,
                $"{plan.DisplayName} requires {plan.Files.Count} managed file(s) that are missing " +
                $"or invalid (for example '{sample}'). Confirm the download in the desktop app or " +
                "download them in Settings > Local Files.");
        }

        Result result;
        try
        {
            result = await interaction(plan,
                    (progress, token) => DownloadCoreAsync(plan, progress, token), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A throwing handler is a bug, but every waiter still deserves a classified Result
            // rather than a faulted shared operation. The message stays sanitized.
            return Result.Failure(AppErrorCodes.InvalidState,
                $"The managed-file confirmation for '{plan.DisplayName}' failed to run " +
                $"({ClassifyError(exception)}).");
        }

        if (result.IsFailure)
        {
            return result;
        }

        // A reported success is only a claim: re-verify the same frozen inputs so a handler
        // that skipped the download cannot turn a missing resource into a success.
        ManagedFilesPlan verified = await planFactory(cancellationToken).ConfigureAwait(false);
        return verified.IsEmpty
            ? Result.Success()
            : Result.Failure(AppErrorCodes.NotFound,
                $"{plan.DisplayName} still lacks {verified.Files.Count} managed file(s) after the " +
                $"confirmed download reported success (for example '{verified.Files[0].RelativePath}').");
    }

    /// <summary>
    /// Downloads each planned file sequentially under a per-destination cross-process lock:
    /// HTTP success only, declared byte length and pinned SHA-256 are verified, a unique
    /// same-directory partial is written and atomically installed. Files that already verify
    /// are skipped; an existing mismatched file is replaced only when the plan flagged it as
    /// confirmed — and then only after its plan-time identity still matches, so a newer user
    /// file is never clobbered. A target created mid-download is never silently overwritten:
    /// an already-satisfying file counts as installed, anything else is a conflict.
    /// Cancellation removes only this call's own partials and never deletes user files or
    /// whole directories.
    /// </summary>
    public Task<Result> DownloadAsync(ManagedFilesPlan plan,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(plan);
        // Admission and tracking are one atomic step: the slot registers before the download
        // factory runs, so a concurrent DisposeAsync can never drain an empty set while work
        // is still being admitted.
        return BeginOperation(token => DownloadCoreAsync(plan, progress, token), cancellationToken);
    }

    private async Task<Result> DownloadCoreAsync(ManagedFilesPlan plan,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (plan.IsEmpty)
        {
            progress?.Report(1);
            return Result.Success();
        }

        IDownloadProgress? rich = progress as IDownloadProgress;
        if (rich is not null)
        {
            foreach (ManagedFileDownload file in plan.Files)
            {
                rich.ReportFile(new DownloadFileProgress(file.RelativePath, "queued", 0,
                    file.ExpectedBytes));
            }
        }

        double totalWeight = plan.Files.Sum(WeightOf);
        double completedWeight = 0;
        foreach (ManagedFileDownload file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double weight = WeightOf(file);
            double baseWeight = completedWeight;

            void Report(double fraction)
            {
                progress?.Report(Math.Min(0.999,
                    (baseWeight + weight * Math.Clamp(fraction, 0, 1)) / totalWeight));
            }

            // The cross-process lock serializes independent hosts writing the same managed
            // destination; verification re-runs inside it so a file another process just
            // installed is never downloaded twice.
            using IDisposable fileLock = await AcquireFileLockAsync(file.DestinationPath,
                cancellationToken).ConfigureAwait(false);
            if (await IsSatisfiedAsync(file.DestinationPath, file.ExpectedBytes, file.Sha256,
                    cancellationToken).ConfigureAwait(false))
            {
                completedWeight += weight;
                Report(1);
                long? existingBytes = null;
                try
                {
                    FileInfo existingInfo = new(file.DestinationPath);
                    existingBytes = existingInfo.Exists ? existingInfo.Length : null;
                }
                catch (Exception exception) when (exception is IOException
                                                      or UnauthorizedAccessException)
                {
                }

                rich?.ReportFile(new DownloadFileProgress(file.RelativePath, "succeeded",
                    existingBytes ?? file.ExpectedBytes ?? 0,
                    file.ExpectedBytes ?? existingBytes));
                continue;
            }

            if (File.Exists(file.DestinationPath) && !file.OverwritesExisting)
            {
                Result conflict = Result.Failure(AppErrorCodes.Conflict,
                    $"'{file.RelativePath}' exists at its destination and does not match the " +
                    "declared manifest. Replacing it needs the explicit confirmation of a " +
                    "managed-file download plan.");
                rich?.ReportFile(new DownloadFileProgress(file.RelativePath, "failed", 0,
                    file.ExpectedBytes, conflict.ErrorMessage));
                return conflict;
            }

            Result downloaded = await DownloadFileAsync(file, Report, progress, cancellationToken)
                .ConfigureAwait(false);
            if (downloaded.IsFailure)
            {
                return downloaded;
            }

            completedWeight += weight;
            Report(1);
        }

        progress?.Report(1);
        return Result.Success();
    }

    private async Task<Result> DownloadFileAsync(ManagedFileDownload file,
        Action<double> report, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        IDownloadProgress? rich = progress as IDownloadProgress;
        if (file.DownloadUrl is null)
        {
            Result missing = Result.Failure(AppErrorCodes.ValidationFailed,
                $"Managed file '{file.RelativePath}' declares no download_url; the host cannot fetch it.");
            rich?.ReportFile(new DownloadFileProgress(file.RelativePath, "failed", 0,
                file.ExpectedBytes, missing.ErrorMessage));
            return missing;
        }

        DownloadFileProgress current = new(file.RelativePath, "running", 0, file.ExpectedBytes);

        void ReportFile(DownloadFileProgress value)
        {
            current = value;
            rich?.ReportFile(value);
        }

        ReportFile(current);
        Result result;
        try
        {
            result = await DownloadFileCoreAsync(file, report, ReportFile, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            rich?.ReportFile(current with { State = "cancelled" });
            throw;
        }

        rich?.ReportFile(result.IsSuccess
            ? current with { State = "succeeded" }
            : current with { State = "failed", ErrorMessage = result.ErrorMessage });
        return result;
    }

    private async Task<Result> DownloadFileCoreAsync(ManagedFileDownload file,
        Action<double> report, Action<DownloadFileProgress> reportFile,
        CancellationToken cancellationToken)
    {
        string target = file.DestinationPath;
        string partial = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using HttpRequestMessage request = new(HttpMethod.Get, file.DownloadUrl);
            // Public model/file hosts 403 anonymous clients — the shipped downloaders identify
            // as Patchouli, and this service must match.
            request.Headers.UserAgent.ParseAdd("Patchouli");
            using CancellationTokenSource bounded =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(RequestTimeout);
            // Unlike HttpMessageInvoker, HttpClient buffers the body unless ResponseHeadersRead is explicit.
            using HttpResponseMessage response = await (_http is HttpClient client
                    ? client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token)
                    : _http.SendAsync(request, bounded.Token))
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure(AppErrorCodes.NetworkError,
                    $"Failed to download '{file.RelativePath}': HTTP {(int)response.StatusCode}.");
            }

            long? declaredLength = response.Content.Headers.ContentLength is > 0
                ? response.Content.Headers.ContentLength
                : null;
            long? progressTotal = file.ExpectedBytes is > 0 ? file.ExpectedBytes : declaredLength;
            reportFile(new DownloadFileProgress(file.RelativePath, "running", 0, progressTotal));

            long downloaded = 0;
            await using (FileStream destination = new(partial, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None))
            {
                await using Stream source = await response.Content
                    .ReadAsStreamAsync(bounded.Token).ConfigureAwait(false);
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, bounded.Token)
                           .ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), bounded.Token)
                        .ConfigureAwait(false);
                    downloaded += read;
                    if (file.ExpectedBytes is { } expected && downloaded > expected)
                    {
                        return Result.Failure(AppErrorCodes.NetworkError,
                            $"Downloaded '{file.RelativePath}' exceeds the declared size.");
                    }

                    if (progressTotal is { } known && known > 0)
                    {
                        report((double)downloaded / known);
                    }
                    else
                    {
                        report(0);
                    }

                    reportFile(new DownloadFileProgress(file.RelativePath, "running",
                        downloaded, progressTotal));
                }

                if (file.ExpectedBytes is { } expectedSize && downloaded != expectedSize)
                {
                    return Result.Failure(AppErrorCodes.NetworkError,
                        $"Downloaded '{file.RelativePath}' is {downloaded} bytes, expected {expectedSize}.");
                }

                if (declaredLength is { } promised && downloaded < promised)
                {
                    return Result.Failure(AppErrorCodes.NetworkError,
                        $"Download of '{file.RelativePath}' ended early at {downloaded} bytes; " +
                        $"the server announced {promised}.");
                }
            }

            reportFile(new DownloadFileProgress(file.RelativePath, "verifying", downloaded,
                progressTotal));
            if (!await IsSatisfiedAsync(partial, file.ExpectedBytes, file.Sha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                return Result.Failure(AppErrorCodes.NetworkError,
                    $"Downloaded '{file.RelativePath}' failed size or SHA-256 verification. Please retry.");
            }

            return await InstallPartialAsync(file, target, partial, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the bounded timeout remains — a genuine caller cancellation rethrows.
            return Result.Failure(AppErrorCodes.NetworkError,
                $"Download of '{file.RelativePath}' timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                              or UnauthorizedAccessException)
        {
            // Messages carry the relative file name only — never the URL's query/credential
            // material or local paths — so results stay safe for public surfaces and logs.
            return Result.Failure(AppErrorCodes.NetworkError,
                $"Download of '{file.RelativePath}' failed: {ClassifyError(exception)}.");
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>
    /// Moves the verified partial onto the destination. Fresh installs use non-overwriting move:
    /// a target that appeared mid-download is a success when it already satisfies the
    /// declaration, otherwise a conflict. Confirmed replacements revalidate the plan-time file
    /// identity so a newer user file between prompt and install is never clobbered.
    /// </summary>
    private async Task<Result> InstallPartialAsync(ManagedFileDownload file, string target,
        string partial, CancellationToken cancellationToken)
    {
        try
        {
            if (!file.OverwritesExisting)
            {
                try
                {
                    File.Move(partial, target, false);
                    return Result.Success();
                }
                catch (IOException)
                {
                    if (await IsSatisfiedAsync(target, file.ExpectedBytes, file.Sha256,
                            cancellationToken).ConfigureAwait(false))
                    {
                        return Result.Success();
                    }

                    return Result.Failure(AppErrorCodes.Conflict,
                        $"'{file.RelativePath}' appeared at its destination during the download " +
                        "and does not match the declared manifest.");
                }
            }

            if (file.ExistingLength is { } existingLength)
            {
                bool changed;
                try
                {
                    FileInfo current = new(target);
                    changed = current.Exists &&
                              (current.Length != existingLength ||
                               current.LastWriteTimeUtc.Ticks != file.ExistingWriteUtcTicks ||
                               (file.ExistingSha256 is { } pinned &&
                                !string.Equals(
                                    await HashFileAsync(target, cancellationToken).ConfigureAwait(false),
                                    pinned, StringComparison.OrdinalIgnoreCase)));
                }
                catch (IOException)
                {
                    // Cannot reverify identity → never overwrite.
                    changed = true;
                }
                catch (UnauthorizedAccessException)
                {
                    changed = true;
                }

                if (changed)
                {
                    return Result.Failure(AppErrorCodes.Conflict,
                        $"'{file.RelativePath}' changed at its destination after the replacement " +
                        "was confirmed; refusing to overwrite a newer user file.");
                }
            }

            File.Move(partial, target, true);
            return Result.Success();
        }
        catch (IOException exception)
        {
            return Result.Failure(AppErrorCodes.NetworkError,
                $"Install of '{file.RelativePath}' failed: {ClassifyError(exception)}.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure(AppErrorCodes.NetworkError,
                $"Install of '{file.RelativePath}' failed: {ClassifyError(exception)}.");
        }
    }

    /// <summary>
    /// The single admission point for all real work: under the lifecycle gate the closed flag is
    /// checked and the operation slot reserved BEFORE the factory is invoked, so a concurrent
    /// shutdown can never pass an empty drain while work is being admitted. The factory runs
    /// outside the gate; the slot is released only when the task actually completes — an
    /// operation that ignores cancellation still holds its slot until its work exits.
    /// </summary>
    private Task<Result> BeginOperation(Func<CancellationToken, Task<Result>> start,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                _shutdownToken);
            _active++;
        }

        Task<Result> operation;
        try
        {
            operation = start(linked.Token);
        }
        catch
        {
            linked.Dispose();
            ReleaseOperation();
            throw;
        }

        _ = operation.ContinueWith(_ =>
            {
                linked.Dispose();
                ReleaseOperation();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return operation;
    }

    private void ReleaseOperation()
    {
        lock (_lifecycle)
        {
            if (--_active == 0 && _closed)
            {
                _drained.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Marks the service closed under the gate and returns the shared drain task — pending while
    /// any admitted operation still runs, so every <see cref="DisposeAsync"/> caller awaits the
    /// same completion.
    /// </summary>
    private Task Close()
    {
        lock (_lifecycle)
        {
            _closed = true;
            if (_active == 0)
            {
                _drained.TrySetResult();
            }

            return _drained.Task;
        }
    }

    /// <summary>
    /// Single-flight identity: the immutable required declaration set (destination, URL, bytes,
    /// digest) under the plan's owner key — never the mutable missing subset. Two different
    /// owners or declaration sets for the same destination therefore form distinct flights,
    /// while concurrent ensures needing the same files share one prompt.
    /// </summary>
    private static string FlightKey(string ownerKey, ManagedFilesPlan plan)
    {
        IReadOnlyList<ManagedFileDownload> required = plan.RequiredFiles.Count > 0
            ? plan.RequiredFiles
            : plan.Files;
        StringBuilder builder = new(ownerKey);
        foreach (ManagedFileDownload file in required.OrderBy(
                     file => file.DestinationPath, PathComparer()))
        {
            builder.Append('\n').Append(file.DownloadUrl ?? "-").Append('|')
                .Append(file.DestinationPath).Append('|')
                .Append(file.ExpectedBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        ?? "-").Append('|')
                .Append(file.Sha256 ?? "-");
        }

        return builder.ToString();
    }

    /// <summary>
    /// A cross-process lock scoped to one managed destination: two host processes (desktop app
    /// and a standalone MCP server sharing the data directory) can download the same file
    /// concurrently. Windows keeps the original named <see cref="Semaphore"/> (a Mutex cannot
    /// be used — release is thread-affine and the pipeline holds the lock across awaits that
    /// resume on arbitrary thread-pool threads); other platforms — and tests selecting the
    /// backend explicitly — use a persistent per-destination coordination file opened with
    /// <see cref="FileShare.None"/>. The file is never unlinked: deleting it would let a second
    /// open land on a different inode and silently split the lock.
    /// </summary>
    private static Task<IDisposable> AcquireFileLockAsync(string destination,
        CancellationToken cancellationToken)
    {
        return AcquireFileLockAsync(destination, SelectFileLockBackend(), cancellationToken);
    }

    /// <summary>The per-destination lock backend for the current platform.</summary>
    internal static ManagedFileLockBackend SelectFileLockBackend()
    {
        return OperatingSystem.IsWindows()
            ? ManagedFileLockBackend.NamedSemaphore
            : ManagedFileLockBackend.LockFile;
    }

    /// <summary>
    /// The cross-process lock mechanism behind <see cref="AcquireFileLockAsync(string,
    /// CancellationToken)"/>: the named semaphore on Windows or the persistent coordination
    /// file elsewhere. Internal so tests can exercise either backend on any host.
    /// </summary>
    internal enum ManagedFileLockBackend
    {
        NamedSemaphore,
        LockFile
    }

    internal static Task<IDisposable> AcquireFileLockAsync(string destination,
        ManagedFileLockBackend backend, CancellationToken cancellationToken)
    {
        return backend == ManagedFileLockBackend.LockFile
            ? AcquireLockFileAsync(destination, cancellationToken)
            : AcquireSemaphoreLockAsync(destination, cancellationToken);
    }

    private static Task<IDisposable> AcquireSemaphoreLockAsync(string destination,
        CancellationToken cancellationToken)
    {
        string name = "patchouli-managed-file-" +
                      Convert.ToHexStringLower(SHA256.HashData(
                          Encoding.UTF8.GetBytes(destination.ToUpperInvariant())));
        // The wait runs off the caller's context: it can outlast a UI thread, and cancellation
        // inside the lambda guarantees the semaphore is always released or disposed.
        return Task.Run<IDisposable>(() =>
        {
            Semaphore gate = new(1, 1, name);
            bool acquired = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (!(acquired = gate.WaitOne(TimeSpan.FromMilliseconds(250))))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // A token cancelled while WaitOne returned true must release, not leak, the
                // acquired count.
                cancellationToken.ThrowIfCancellationRequested();
                return new FileLockReleaser(gate);
            }
            catch
            {
                if (acquired)
                {
                    gate.Release();
                }

                gate.Dispose();
                throw;
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Persistent lock-file backend: the canonical destination plus a <c>.patchouli.lock</c>
    /// suffix, opened exclusively and retried only while the OS reports a sharing violation —
    /// other IO or permission failures surface immediately instead of retrying forever.
    /// </summary>
    private static async Task<IDisposable> AcquireLockFileAsync(string destination,
        CancellationToken cancellationToken)
    {
        string lockPath = Path.GetFullPath(destination) + ".patchouli.lock";
        cancellationToken.ThrowIfCancellationRequested();
        string? parent = Path.GetDirectoryName(lockPath);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream stream;
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            // A token cancelled while the open succeeded must not leave the lock held.
            if (cancellationToken.IsCancellationRequested)
            {
                stream.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return stream;
        }
    }

    /// <summary>Sharing-violation codes — the only lock-open failures worth retrying.</summary>
    private static bool IsSharingViolation(IOException exception)
    {
        return IsSharingViolationCode(exception.HResult, OperatingSystem.IsWindows());
    }

    /// <summary>
    /// The retry-eligible open failure classifier: Windows reports ERROR_SHARING_VIOLATION
    /// (0x80070020); Unix flock failures surface as raw errno EAGAIN (11, Linux) or
    /// EWOULDBLOCK (35, macOS) — as a bare HResult or in the low word of an HRESULT — and only
    /// on non-Windows where that backend runs. Everything else is a real IO/permission failure
    /// and must not be retried. Internal so tests can cover both platform branches.
    /// </summary>
    internal static bool IsSharingViolationCode(int hresult, bool windows)
    {
        if (hresult == unchecked((int)0x80070020))
        {
            return true;
        }

        if (windows)
        {
            return false;
        }

        int code = (hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
            ? hresult & 0xFFFF
            : hresult;
        return code is 11 or 35;
    }

    private sealed class FileLockReleaser(Semaphore gate) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                gate.Release();
            }
            catch (SemaphoreFullException)
            {
                // Defensive: a release-after-release is a bug, but must not crash teardown.
            }
            finally
            {
                gate.Dispose();
            }
        }
    }

    /// <summary>
    /// One shared ensure operation with its own cancellation scope. Join, withdraw, the
    /// cancel-requested flag and operation publication are all atomic under a single gate — a
    /// caller is either registered on a live flight or rejected before its slot can race the
    /// last-waiter cancel — and the gate never holds arbitrary work (the operation factory only
    /// runs to its first real await inside it). The operation materializes on first registered
    /// access, so a losing GetOrAdd flight allocates nothing and never runs. The cancellation
    /// source is created only when the operation starts and disposed after the last waiter
    /// drains it.
    /// </summary>
    private sealed class EnsureFlight
    {
        private readonly object _gate = new();
        private readonly Func<CancellationToken, Task<Result>> _run;
        private readonly CancellationToken _shutdownToken;
        private CancellationTokenSource? _cancellation;
        private Task<Result>? _operation;
        private int _waiters;
        private bool _cancelRequested;

        public EnsureFlight(Func<CancellationToken, Task<Result>> run,
            CancellationToken shutdownToken)
        {
            _run = run;
            _shutdownToken = shutdownToken;
        }

        /// <summary>True once the flight is unusable: cancel-requested or its operation finished.</summary>
        public bool IsDead
        {
            get
            {
                lock (_gate)
                {
                    return _cancelRequested || _operation?.IsCompleted == true;
                }
            }
        }

        /// <summary>
        /// True when the flight's own cancellation was requested (orphan or shutdown path).
        /// A shared operation that ends cancelled without this flag is a real user cancellation
        /// for every waiter — not an orphan to retry.
        /// </summary>
        public bool CancelRequested
        {
            get
            {
                lock (_gate)
                {
                    return _cancelRequested;
                }
            }
        }

        /// <summary>Atomically registers a waiter on a live flight; false when it is dead.</summary>
        public bool TryJoin()
        {
            lock (_gate)
            {
                if (_cancelRequested)
                {
                    return false;
                }

                _waiters++;
                return true;
            }
        }

        /// <summary>
        /// Publishes the shared operation exactly once, called only by a registered waiter. A
        /// synchronous start failure poisons the flight instead of caching a fault no one can
        /// observe; a cancelled flight never starts work — its waiters retry on a fresh flight.
        /// </summary>
        public Task<Result> GetOrStartOperation()
        {
            lock (_gate)
            {
                if (_cancelRequested)
                {
                    throw new OperationCanceledException();
                }

                if (_operation is not null)
                {
                    return _operation;
                }

                _cancellation ??= CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
                try
                {
                    _operation = _run(_cancellation.Token);
                }
                catch
                {
                    _cancelRequested = true;
                    throw;
                }

                return _operation;
            }
        }

        /// <summary>
        /// Withdraws one waiter under the gate. When the last leaves, the flight is
        /// cancel-requested and its operation (if it ever started) is returned for the caller to
        /// cancel and drain before reporting cancellation upstream.
        /// </summary>
        public Task<Result>? Withdraw(out bool wasLast)
        {
            lock (_gate)
            {
                _waiters--;
                wasLast = _waiters == 0;
                if (wasLast)
                {
                    _cancelRequested = true;
                }

                return _operation;
            }
        }

        /// <summary>Fires the flight's own cancellation after the last waiter withdrew.</summary>
        public void RequestCancel()
        {
            CancellationTokenSource? cancellation;
            lock (_gate)
            {
                _cancelRequested = true;
                cancellation = _cancellation;
            }

            cancellation?.Cancel();
        }

        /// <summary>Releases the flight's cancellation source once no waiter can observe it.</summary>
        public void DisposeCancellation()
        {
            lock (_gate)
            {
                if (_waiters == 0)
                {
                    _cancellation?.Dispose();
                    _cancellation = null;
                }
            }
        }
    }

    /// <summary>
    /// Plan-time identity + verification for one resolved declaration: the download record
    /// carries the existing file's length/mtime/digest so a confirmed replacement revalidates
    /// before installing — a newer user file is never clobbered.
    /// </summary>
    internal static async Task<(ManagedFileDownload Download, bool Satisfied)>
        DescribeExistingAsync(ManagedFileTarget target, CancellationToken cancellationToken)
    {
        FileInfo info = new(target.DestinationPath);
        bool exists = info.Exists;
        long? length = null;
        long? writeTicks = null;
        string? existingSha = null;
        bool satisfied = false;
        if (exists)
        {
            // One metadata snapshot captures the plan-time identity; the confirmed replacement
            // revalidates it before installing so a newer user file is never clobbered.
            length = info.Length;
            writeTicks = info.LastWriteTimeUtc.Ticks;
            bool sizeMatches = target.ExpectedBytes is not { } expected || length == expected;
            if (sizeMatches && target.Sha256 is null)
            {
                satisfied = true;
            }
            else
            {
                // The existing bytes are hashed once here: it both verifies a declared digest
                // and pins the mismatched file's identity for the confirmed-replacement check —
                // length+timestamp alone cannot spot a same-size rewrite that kept the mtime.
                try
                {
                    existingSha = await HashFileAsync(target.DestinationPath, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (IOException)
                {
                    existingSha = null;
                }
                catch (UnauthorizedAccessException)
                {
                    existingSha = null;
                }

                satisfied = sizeMatches && target.Sha256 is not null && existingSha is not null &&
                            string.Equals(existingSha, target.Sha256, StringComparison.OrdinalIgnoreCase);
            }

            if (satisfied)
            {
                existingSha = null;
            }
        }

        ManagedFileDownload download = new(target.RelativePath, target.DownloadUrl,
            target.DestinationPath, target.ExpectedBytes, target.Sha256, target.Version)
        {
            OverwritesExisting = exists && !satisfied,
            ExistingLength = length,
            ExistingWriteUtcTicks = writeTicks,
            ExistingSha256 = existingSha
        };
        return (download, satisfied);
    }

    /// <summary>SHA-256 of a file's actual bytes — lowercase hex for comparison and pinning.</summary>
    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Synchronous digest for the static status/local-path surface.</summary>
    internal static string HashFileSync(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Synchronous <see cref="IsSatisfiedAsync"/> for status reads.</summary>
    internal static bool IsSatisfiedSync(string path, long? expectedBytes, string? sha256)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (expectedBytes is { } expected && new FileInfo(path).Length != expected)
            {
                return false;
            }

            if (sha256 is null)
            {
                return true;
            }

            return string.Equals(HashFileSync(path), sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task<bool> IsSatisfiedAsync(string path, long? expectedBytes,
        string? sha256, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (expectedBytes is { } expected && new FileInfo(path).Length != expected)
            {
                return false;
            }

            if (sha256 is null)
            {
                return true;
            }

            return string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false),
                sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            // A file we cannot read cannot verify — treat it as not satisfied so planning and
            // install surface a classified conflict instead of crashing the caller.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static double WeightOf(ManagedFileDownload file)
    {
        return file.ExpectedBytes is > 0 ? file.ExpectedBytes.Value : 1;
    }

    private static string ClassifyError(Exception exception)
    {
        return exception switch
        {
            HttpRequestException http => DownloadNetworkErrors.Describe(http),
            UnauthorizedAccessException => "access denied",
            IOException => "I/O error",
            _ => "internal error"
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup is best effort and must not hide the original download failure.
        }
        catch (UnauthorizedAccessException)
        {
            // The original failure is still returned if temporary-file cleanup is denied.
        }
    }

    /// <summary>Destination dedup/order comparison matching the platform's file-name semantics.</summary>
    public static StringComparer PathComparer()
    {
        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    /// <summary>Destination path equality matching the platform's file-name semantics.</summary>
    public static StringComparison PathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed), this);
    }

    /// <summary>
    /// Requests shutdown without blocking: no new operations are admitted and every in-flight
    /// operation observes cancellation. The owned transport is released only by
    /// <see cref="DisposeAsync"/>, after actual network/file work drains — the host awaits that
    /// path on teardown.
    /// </summary>
    public void Dispose()
    {
        _ = BeginShutdown();
    }

    /// <summary>
    /// Closes the service, cancels all admitted operations and waits for their real network/file
    /// work to exit — including sources that ignore cancellation — before the owned transport is
    /// released. Every concurrent caller awaits the same pending drain task; a prior
    /// <see cref="Dispose"/> only requested shutdown and never short-circuits this drain.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        return new ValueTask(BeginShutdown());
    }

    private Task BeginShutdown()
    {
        Task drain;
        lock (_lifecycle)
        {
            if (_shutdownStarted)
            {
                return _shutdownCompleted.Task;
            }

            _shutdownStarted = true;
            drain = Close();
        }

        _ = FinishShutdownAsync(drain);
        return _shutdownCompleted.Task;
    }

    private async Task FinishShutdownAsync(Task drain)
    {
        try
        {
            try
            {
                // CancelAsync keeps even a blocking registration off the disposing UI caller.
                await _shutdown.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await drain.ConfigureAwait(false);
                _shutdown.Dispose();
                if (_ownsHttp)
                {
                    _http.Dispose();
                }
            }

            _shutdownCompleted.TrySetResult();
        }
        catch (Exception exception)
        {
            _shutdownCompleted.TrySetException(exception);
        }
    }
}
