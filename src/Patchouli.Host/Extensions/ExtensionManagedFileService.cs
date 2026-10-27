using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.UI;

namespace Patchouli.Host.Extensions;

/// <summary>
/// One file the host must fetch for a plan to be satisfied. <see cref="DestinationPath"/> is the
/// canonical local path — either the managed default directory or the user's configured override.
/// </summary>
public sealed record ExtensionManagedFileDownload(
    string RelativePath,
    string DownloadUrl,
    string DestinationPath,
    long? ExpectedBytes = null,
    string? Sha256 = null)
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
/// The missing/invalid managed files for one extension under a resolved configuration and scope.
/// Internal host/UI resource — destination paths are never serialized into MCP payloads.
/// </summary>
public sealed record ExtensionManagedFilesPlan(
    string ExtensionId,
    string DisplayName,
    ExtensionConfigSnapshot ResolvedConfig,
    IReadOnlyList<ExtensionManagedFileDownload> Files)
{
    public bool IsEmpty => Files.Count == 0;

    /// <summary>Total size across files whose declarations pin a byte count.</summary>
    public long TotalExpectedBytes => Files.Sum(file => file.ExpectedBytes ?? 0);

    /// <summary>
    /// The complete required declaration set for this plan — every applicable file whether it is
    /// already satisfied or missing. Single-flight identity is derived from this immutable set
    /// (URL/target/bytes/digest), so concurrent ensures join by what is required rather than by
    /// the mutable missing subset. Empty for hand-built plans; the identity then uses
    /// <see cref="Files"/>.
    /// </summary>
    public IReadOnlyList<ExtensionManagedFileDownload> RequiredFiles { get; init; } = [];
}

/// <summary>
/// One managed-file directory group for inventory UI: all declared destinations that land in the
/// same local directory, plus the subset currently failing verification. Produced by
/// <see cref="ExtensionManagedFileService.DescribeGroupsAsync"/> for manual management — every
/// declared group appears regardless of the currently selected conditions or scope.
/// </summary>
public sealed record ExtensionManagedFileGroup(
    string ExtensionId,
    string FieldKey,
    string FieldLabel,
    string? DirectoryKind,
    string Directory,
    IReadOnlyList<ExtensionManagedFileDownload> Files,
    IReadOnlyList<ExtensionManagedFileDownload> Missing)
{
    public bool IsComplete => Missing.Count == 0;

    /// <summary>Total declared size across the group's files when byte counts are pinned.</summary>
    public long TotalExpectedBytes => Files.Sum(file => file.ExpectedBytes ?? 0);
}

/// <summary>
/// Optional desktop confirmation boundary for <see cref="ExtensionManagedFileService.EnsureAsync"/>.
/// The handler owns the confirmation UX (dialog, modal progress, cancellation); the supplied
/// <paramref name="download"/> delegate is the service's own verified, atomic download — the
/// handler must call it to produce files and must return its result (or decline/cancel).
/// </summary>
public interface IExtensionManagedFileInteraction
{
    Task<Result> ConfirmAndDownloadAsync(
        ExtensionManagedFilesPlan plan,
        Func<IProgress<double>?, CancellationToken, Task<Result>> download,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Generic host-managed extension files: injects FSX-declared managed-path defaults, discovers
/// actually missing/invalid required files for a configuration and scope, and performs confirmed,
/// verified, atomic downloads. Transport is used only after confirmation — schema reads,
/// default injection, planning and inventory never touch the network, create directories, or run
/// package code. Destination rules: a known <c>DirectoryKind</c> resolves through the supplied
/// directory metadata (e.g. <c>OcrStorageLocations.ModelDirectoryFor</c>); unknown or absent
/// kinds land below <c>{modelsRoot}/extensions/{extensionId}/{fieldKey}</c>; a nonempty custom
/// field value always wins over injected defaults — and a stored value equal to any declared
/// managed destination is recognized as a previously injected default, so selection changes
/// re-resolve it rather than treating it as a user override.
/// </summary>
public sealed class ExtensionManagedFileService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);

    private readonly string _modelsRoot;
    private readonly Func<string, string?>? _directoryResolver;
    private readonly HttpMessageInvoker _http;
    private readonly bool _ownsHttp;

    // One in-flight ensure per immutable required-declaration set: a second caller for the same
    // file set awaits the same confirmation+download instead of stacking prompts per page/task.
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

    public ExtensionManagedFileService(string modelsRoot,
        Func<string, string?>? legacyDirectoryResolver = null,
        HttpMessageInvoker? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        _modelsRoot = Path.GetFullPath(modelsRoot);
        _directoryResolver = legacyDirectoryResolver;
        // Captured once: the struct stays valid after the source is disposed, so an operation
        // racing teardown can still link its token without touching a disposed source.
        _shutdownToken = _shutdown.Token;
        _ownsHttp = http is null;
        _http = http ?? new HttpMessageInvoker(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        }, true);
    }

    /// <summary>Optional desktop interaction; when unset, missing files report an actionable failure — never an automatic download.</summary>
    public IExtensionManagedFileInteraction? Interaction { get; set; }

    /// <summary>
    /// Injects declared managed-path defaults for <see cref="ConfigValueKind.FilePath"/>/
    /// <see cref="ConfigValueKind.DirectoryPath"/> fields. A stored value equal to any declared
    /// managed destination is a previously injected default and is re-resolved for the current
    /// selection — a genuine custom path is preserved. Metadata/path work only: no directories
    /// are created, no network or model loads happen.
    /// </summary>
    public ExtensionConfigSnapshot ResolveDefaults(string extensionId,
        IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config)
    {
        ThrowIfClosed();
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(config);

        Dictionary<string, string> resolved = new(config.Values, StringComparer.Ordinal);
        foreach (ConfigFieldDescriptor field in fields)
        {
            if (field.ManagedFiles is not { Count: > 0 } managedFiles ||
                field.Kind is not (ConfigValueKind.FilePath or ConfigValueKind.DirectoryPath))
            {
                continue;
            }

            if (CustomOverride(extensionId, field, managedFiles, config) is { } custom)
            {
                resolved[field.Key] = custom;
                continue;
            }

            string? declared = EffectiveValue(field.Key, fields, resolved);
            ExtensionManagedFile? selected = managedFiles.FirstOrDefault(file =>
                ConditionMatches(file.WhenKey, file.WhenValue, field.Key, fields, resolved, declared));
            if (selected is null)
            {
                resolved[field.Key] = field.DefaultValue ?? "";
                continue;
            }

            resolved[field.Key] = field.Kind == ConfigValueKind.DirectoryPath
                ? ResolveBaseDirectory(extensionId, field.Key, selected.DirectoryKind)
                : ResolveFileDestination(extensionId, field.Key, selected);
        }

        return new ExtensionConfigSnapshot(resolved, config.Revision);
    }

    /// <summary>
    /// The actually missing/invalid required files under the resolved configuration and scope.
    /// Only the file a selection condition chooses and requirement conditions/scopes admit is
    /// evaluated; a null scope cannot disprove a declared scope list. Reads file length and
    /// digest only.
    /// </summary>
    public async Task<ExtensionManagedFilesPlan> PlanAsync(string extensionId,
        IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config, string? scope = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(config);
        ExtensionConfigSnapshot resolved = ResolveDefaults(extensionId, fields, config);
        List<ExtensionManagedFileDownload> required = [];
        List<ExtensionManagedFileDownload> downloads = [];
        HashSet<string> destinations = new(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigFieldDescriptor field in fields)
        {
            if (field.ManagedFiles is not { Count: > 0 } managedFiles ||
                field.Kind is not (ConfigValueKind.FilePath or ConfigValueKind.DirectoryPath))
            {
                continue;
            }

            // Custom-vs-managed is decided by the same shared rule as ResolveDefaults: a stored
            // path equal to any declared managed destination is the injected default, so a
            // model-selection change re-resolves the new file instead of aiming its bytes at the
            // old default path.
            string? custom = CustomOverride(extensionId, field, managedFiles, config);
            foreach (ExtensionManagedFile file in managedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ConditionMatches(file.WhenKey, file.WhenValue, field.Key, fields,
                        resolved.Values, custom ?? EffectiveValue(field.Key, fields, resolved.Values)) ||
                    !ConditionMatches(file.RequiredWhenKey, file.RequiredWhenValue, field.Key, fields,
                        resolved.Values,
                        custom ?? EffectiveValue(field.Key, fields, resolved.Values)) ||
                    !ScopeApplies(file.RequiredScopes, scope))
                {
                    continue;
                }

                string destination = ResolveDestination(extensionId, field, file, custom);
                if (!destinations.Add(destination))
                {
                    continue;
                }

                (ExtensionManagedFileDownload download, bool satisfied) =
                    await DescribeExistingAsync(file, destination, cancellationToken)
                        .ConfigureAwait(false);
                required.Add(download);
                if (!satisfied)
                {
                    downloads.Add(download);
                }
            }
        }

        return new ExtensionManagedFilesPlan(extensionId, extensionId, resolved,
            downloads.AsReadOnly())
        {
            RequiredFiles = required.AsReadOnly()
        };
    }

    /// <summary>
    /// Ensures the plan's files exist, asking the optional interaction once per required set.
    /// An empty plan succeeds; missing files without an interaction report an actionable
    /// resource-missing failure — no caller ever triggers an unconfirmed download. Concurrent
    /// ensures for the same required set share one confirmation+download; each caller's
    /// cancellation only withdraws that caller, and the shared operation is cancelled once no
    /// waiters remain. A successful interaction is verified against the same frozen
    /// fields/config/scope before success is reported.
    /// </summary>
    public async Task<Result> EnsureAsync(string extensionId,
        IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config, string? scope = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ExtensionManagedFilesPlan plan =
            await PlanAsync(extensionId, fields, config, scope, cancellationToken).ConfigureAwait(false);
        if (plan.IsEmpty)
        {
            return Result.Success();
        }

        string key = FlightKey(plan, scope);
        while (true)
        {
            EnsureFlight flight = JoinFlight(key, extensionId, fields, config, scope);
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
                    // before this caller's cancellation is visible upstream — an OCR task must
                    // not report cancelled while its own downloader still runs.
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

    private EnsureFlight JoinFlight(string key, string extensionId,
        IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config, string? scope)
    {
        while (true)
        {
            // The flight token is also linked to shutdown inside BeginOperation, which registers
            // the operation slot under the lifecycle gate before any work can start.
            EnsureFlight flight = GetOrCreateFlight(key,
                token => BeginOperation(inner => RunEnsureAsync(extensionId, fields, config, scope,
                    inner), token));
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

    private async Task<Result> RunEnsureAsync(string extensionId,
        IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config, string? scope,
        CancellationToken cancellationToken)
    {
        // The flight key is the immutable required set, not the missing subset — a caller whose
        // plan predates a shared operation installing files can still arrive here. Re-plan the
        // same frozen inputs so an already-satisfied set never prompts or downloads again.
        ExtensionManagedFilesPlan plan =
            await PlanAsync(extensionId, fields, config, scope, cancellationToken)
                .ConfigureAwait(false);
        if (plan.IsEmpty)
        {
            return Result.Success();
        }

        if (Interaction is not { } interaction)
        {
            string sample = Path.GetFileName(plan.Files[0].DestinationPath);
            return Result.Failure(AppErrorCodes.NotFound,
                $"{plan.DisplayName} requires {plan.Files.Count} managed file(s) that are missing " +
                $"or invalid (for example '{sample}'). Download them in Settings > Local Files, or " +
                "confirm the download when the desktop app prompts for it.");
        }

        Result result;
        try
        {
            result = await interaction.ConfirmAndDownloadAsync(plan,
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

        // A reported success is only a claim: re-verify the same frozen declaration/config/scope
        // so a handler that skipped the download cannot turn a missing resource into a runnable
        // task. This intentionally re-reads the plan inputs, never the latest package source.
        ExtensionManagedFilesPlan verified =
            await PlanAsync(extensionId, fields, config, scope, cancellationToken)
                .ConfigureAwait(false);
        return verified.IsEmpty
            ? Result.Success()
            : Result.Failure(AppErrorCodes.NotFound,
                $"{plan.DisplayName} still lacks {verified.Files.Count} managed file(s) after the " +
                "confirmed download reported success (for example '" +
                Path.GetFileName(verified.Files[0].DestinationPath) + "').");
    }

    /// <summary>
    /// Downloads each planned file sequentially: HTTP success only, declared byte length and
    /// pinned SHA-256 are verified, a unique same-directory partial is written and atomically
    /// installed. Files that already verify are skipped; an existing mismatched file is replaced
    /// only when the plan flagged it as confirmed — and then only after its plan-time identity
    /// still matches, so a newer user file is never clobbered. A target created mid-download is
    /// never silently overwritten: an already-satisfying file counts as installed, anything else
    /// is a conflict. Cancellation removes only this call's own partials and never deletes user
    /// files or whole directories.
    /// </summary>
    public Task<Result> DownloadAsync(ExtensionManagedFilesPlan plan,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(plan);
        // Admission and tracking are one atomic step: the slot registers before the download
        // factory runs, so a concurrent DisposeAsync can never drain an empty set while work
        // is still being admitted.
        return BeginOperation(token => DownloadCoreAsync(plan, progress, token), cancellationToken);
    }

    private async Task<Result> DownloadCoreAsync(ExtensionManagedFilesPlan plan,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (plan.IsEmpty)
        {
            progress?.Report(1);
            return Result.Success();
        }

        double totalWeight = plan.Files.Sum(file => WeightOf(file));
        double completedWeight = 0;
        foreach (ExtensionManagedFileDownload file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double weight = WeightOf(file);
            double baseWeight = completedWeight;

            void Report(double fraction)
            {
                progress?.Report(Math.Min(0.999,
                    (baseWeight + weight * Math.Clamp(fraction, 0, 1)) / totalWeight));
            }

            if (await IsSatisfiedAsync(file.DestinationPath, file.ExpectedBytes, file.Sha256,
                    cancellationToken).ConfigureAwait(false))
            {
                completedWeight += weight;
                Report(1);
                continue;
            }

            if (File.Exists(file.DestinationPath) && !file.OverwritesExisting)
            {
                return Result.Failure(AppErrorCodes.Conflict,
                    $"'{file.RelativePath}' exists at its destination and does not match the " +
                    "declared manifest. Replacing it needs the explicit confirmation of a " +
                    "managed-file download plan.");
            }

            Result downloaded = await DownloadFileAsync(file, Report, cancellationToken)
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

    private async Task<Result> DownloadFileAsync(ExtensionManagedFileDownload file,
        Action<double> report, CancellationToken cancellationToken)
    {
        string target = file.DestinationPath;
        string partial = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using HttpRequestMessage request = new(HttpMethod.Get, file.DownloadUrl);
            // Public model hosts (ModelScope among them) 403 anonymous clients — the shipped
            // downloaders identify as Patchouli, and this service must match.
            request.Headers.UserAgent.ParseAdd("Patchouli");
            using CancellationTokenSource bounded =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(RequestTimeout);
            using HttpResponseMessage response = await _http.SendAsync(request, bounded.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure(AppErrorCodes.NetworkError,
                    $"Failed to download '{file.RelativePath}': HTTP {(int)response.StatusCode}.");
            }

            await using (FileStream destination = new(partial, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None))
            {
                await using Stream source = await response.Content
                    .ReadAsStreamAsync(bounded.Token).ConfigureAwait(false);
                byte[] buffer = new byte[81920];
                long downloaded = 0;
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

                    report(file.ExpectedBytes is { } expected2 && expected2 > 0
                        ? (double)downloaded / expected2
                        : 0);
                }

                if (file.ExpectedBytes is { } expectedSize && downloaded != expectedSize)
                {
                    return Result.Failure(AppErrorCodes.NetworkError,
                        $"Downloaded '{file.RelativePath}' is {downloaded} bytes, expected {expectedSize}.");
                }
            }

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
    private async Task<Result> InstallPartialAsync(ExtensionManagedFileDownload file, string target,
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
    /// Inventory grouping for manual file management: every declared directory group — under
    /// managed defaults or the user's custom override — with all declared files and the subset
    /// currently missing or invalid. Requirement conditions and scopes do not hide groups: the
    /// user manages declarations, not the currently selected run. Metadata + disk reads only.
    /// </summary>
    public async Task<IReadOnlyList<ExtensionManagedFileGroup>> DescribeGroupsAsync(
        string extensionId, IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(config);
        ExtensionConfigSnapshot resolved = ResolveDefaults(extensionId, fields, config);
        List<ExtensionManagedFileGroup> groups = [];
        foreach (ConfigFieldDescriptor field in fields)
        {
            if (field.ManagedFiles is not { Count: > 0 } managedFiles ||
                field.Kind is not (ConfigValueKind.FilePath or ConfigValueKind.DirectoryPath))
            {
                continue;
            }

            string? custom = CustomOverride(extensionId, field, managedFiles, config);
            // Under a custom override only the selected model variant may own the target(s).
            // Different variants can declare the same relative filename in a custom directory;
            // installing both would overwrite one model with another. Optional requirement
            // conditions still do not hide inventory; managed defaults show all variant groups.
            IEnumerable<ExtensionManagedFile> applicable = custom is not null
                ? managedFiles.Where(file => ConditionMatches(file.WhenKey, file.WhenValue,
                    field.Key, fields, resolved.Values, custom))
                : managedFiles;
            foreach (IGrouping<string, (ExtensionManagedFile File, string Destination)> directory in
                     applicable.Select(file => (File: file,
                             Destination: ResolveDestination(extensionId, field, file, custom)))
                         .GroupBy(pair => Path.GetDirectoryName(pair.Destination) ?? pair.Destination,
                             StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ExtensionManagedFile File, string Destination)[] items = directory.ToArray();
                List<ExtensionManagedFileDownload> files = [];
                List<ExtensionManagedFileDownload> missing = [];
                foreach ((ExtensionManagedFile declared, string destination) in items)
                {
                    (ExtensionManagedFileDownload download, bool satisfied) =
                        await DescribeExistingAsync(declared, destination, cancellationToken)
                            .ConfigureAwait(false);
                    files.Add(download);
                    if (!satisfied)
                    {
                        missing.Add(download);
                    }
                }

                groups.Add(new ExtensionManagedFileGroup(extensionId, field.Key, field.Label,
                    items.Select(item => item.File.DirectoryKind).FirstOrDefault(kind => kind is not null),
                    directory.Key, files.AsReadOnly(), missing.AsReadOnly()));
            }
        }

        return groups.AsReadOnly();
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
    /// digest) — never the mutable missing subset. Two different manifests for the same
    /// destination therefore form distinct flights, while concurrent ensures needing the same
    /// files share one prompt.
    /// </summary>
    private static string FlightKey(ExtensionManagedFilesPlan plan, string? scope)
    {
        IReadOnlyList<ExtensionManagedFileDownload> required = plan.RequiredFiles.Count > 0
            ? plan.RequiredFiles
            : plan.Files;
        StringBuilder builder = new StringBuilder(plan.ExtensionId).Append('\n').Append(scope ?? "");
        foreach (ExtensionManagedFileDownload file in required.OrderBy(
                     file => file.DestinationPath, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('\n').Append(file.DownloadUrl).Append('|')
                .Append(file.DestinationPath).Append('|')
                .Append(file.ExpectedBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        ?? "-").Append('|')
                .Append(file.Sha256 ?? "-");
        }

        return builder.ToString();
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

    private static double WeightOf(ExtensionManagedFileDownload file)
    {
        return file.ExpectedBytes is > 0 ? file.ExpectedBytes.Value : 1;
    }

    private static async Task<(ExtensionManagedFileDownload Download, bool Satisfied)>
        DescribeExistingAsync(ExtensionManagedFile file, string destination,
            CancellationToken cancellationToken)
    {
        FileInfo info = new(destination);
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
            bool sizeMatches = file.ExpectedBytes is not { } expected || length == expected;
            if (sizeMatches && file.Sha256 is null)
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
                    existingSha = await HashFileAsync(destination, cancellationToken)
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

                satisfied = sizeMatches && file.Sha256 is not null && existingSha is not null &&
                            string.Equals(existingSha, file.Sha256, StringComparison.OrdinalIgnoreCase);
            }

            if (satisfied)
            {
                existingSha = null;
            }
        }

        ExtensionManagedFileDownload download = new(file.RelativePath, file.DownloadUrl, destination,
            file.ExpectedBytes, file.Sha256)
        {
            OverwritesExisting = exists && !satisfied,
            ExistingLength = length,
            ExistingWriteUtcTicks = writeTicks,
            ExistingSha256 = existingSha
        };
        return (download, satisfied);
    }

    /// <summary>SHA-256 of a file's actual bytes — lowercase hex for comparison and pinning.</summary>
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<bool> IsSatisfiedAsync(string path, long? expectedBytes, string? sha256,
        CancellationToken cancellationToken)
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

    /// <summary>
    /// The shared custom-vs-managed rule: a nonempty stored value is a genuine custom override
    /// unless it equals one of the field's declared managed destinations — in which case it is
    /// the injected default and re-resolves whenever the selected declaration changes. Directory
    /// fields compare the managed base directory; file fields compare each declared file's
    /// managed destination.
    /// </summary>
    private string? CustomOverride(string extensionId, ConfigFieldDescriptor field,
        IReadOnlyList<ExtensionManagedFile> managedFiles, ExtensionConfigSnapshot config)
    {
        if (!config.Values.TryGetValue(field.Key, out string? stored) ||
            string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        string canonical = Path.GetFullPath(stored.Trim());
        bool managed = managedFiles.Any(file => field.Kind == ConfigValueKind.DirectoryPath
            ? string.Equals(canonical, ResolveBaseDirectory(extensionId, field.Key, file.DirectoryKind),
                StringComparison.OrdinalIgnoreCase)
            : string.Equals(canonical, ResolveFileDestination(extensionId, field.Key, file),
                StringComparison.OrdinalIgnoreCase));
        return managed ? null : canonical;
    }

    /// <summary>
    /// Configuration value for condition evaluation: the stored value wins; the declared default
    /// supplies the rest. Requirement paths keep this pure — defaults are field metadata, never
    /// computed by package code.
    /// </summary>
    private string? EffectiveValue(string key, IReadOnlyList<ConfigFieldDescriptor> fields,
        IReadOnlyDictionary<string, string> resolved)
    {
        if (resolved.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return fields.FirstOrDefault(field => field.Key == key)?.DefaultValue;
    }

    private bool ConditionMatches(string? conditionKey, string? conditionValue, string fieldKey,
        IReadOnlyList<ConfigFieldDescriptor> fields, IReadOnlyDictionary<string, string> resolved,
        string? fieldValue)
    {
        if (conditionKey is null)
        {
            return true;
        }

        string? actual = conditionKey == fieldKey
            ? fieldValue
            : EffectiveValue(conditionKey, fields, resolved);
        return string.Equals(actual?.Trim(), conditionValue?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ScopeApplies(IReadOnlyList<string>? requiredScopes, string? scope)
    {
        return requiredScopes is not { Count: > 0 } || scope is null ||
               requiredScopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
    }

    private string ResolveBaseDirectory(string extensionId, string fieldKey, string? directoryKind)
    {
        string? resolved = string.IsNullOrWhiteSpace(directoryKind)
            ? null
            : _directoryResolver?.Invoke(directoryKind);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(resolved)
            ? Path.Combine(_modelsRoot, "extensions",
                OcrStorageLocations.SanitizePathSegment(extensionId),
                OcrStorageLocations.SanitizePathSegment(fieldKey))
            : resolved);
    }

    private string ResolveFileDestination(string extensionId, string fieldKey, ExtensionManagedFile file)
    {
        string baseDirectory = ResolveBaseDirectory(extensionId, fieldKey, file.DirectoryKind);
        return Path.GetFullPath(Path.Combine(baseDirectory,
            file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private string ResolveDestination(string extensionId, ConfigFieldDescriptor field,
        ExtensionManagedFile file, string? custom)
    {
        if (custom is not null)
        {
            return Path.GetFullPath(field.Kind == ConfigValueKind.FilePath
                ? custom
                : Path.Combine(custom, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        return ResolveFileDestination(extensionId, field.Key, file);
    }

    private static string ClassifyError(Exception exception)
    {
        return exception switch
        {
            HttpRequestException => "network error",
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
