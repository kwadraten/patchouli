using System.Net;
using Patchouli.Core.Results;
using Patchouli.Host.ManagedFiles;
using Patchouli.UI;

namespace Patchouli.Host.Plugins;

/// <summary>
/// One plugin-declared managed file the host must fetch for a plan to be satisfied.
/// <see cref="DestinationPath"/> is the canonical local path — always
/// <c>{filesRoot}/{pluginId}/{declared path}</c>; it is internal host state and is never
/// serialized into MCP payloads.
/// </summary>
public sealed record PluginManagedFileDownload(
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
/// The missing/invalid managed files for one plugin under its validated manifest.
/// Internal host/UI resource — destination paths are never serialized into MCP payloads.
/// </summary>
public sealed record PluginManagedFilesPlan(
    string PluginId,
    string DisplayName,
    IReadOnlyList<PluginManagedFileDownload> Files)
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
    public IReadOnlyList<PluginManagedFileDownload> RequiredFiles { get; init; } = [];
}

/// <summary>
/// Public per-file status for one declared managed file. Carries declaration metadata and
/// verification booleans only — local filesystem locations are never exposed through it.
/// </summary>
public sealed record PluginManagedFileStatus(
    string Path,
    bool Ready,
    bool Exists,
    long? Bytes,
    string? Sha256,
    long? ExpectedBytes,
    bool Missing,
    bool Mismatch,
    string? DownloadUrl,
    string? Version);

/// <summary>
/// Public status snapshot for all managed files one plugin declares; the UI bridge and the
/// own-plugin local-path surface consume it. Paths stay manifest-relative.
/// </summary>
public sealed record PluginManagedFilesStatus(
    string PluginId,
    string DisplayName,
    IReadOnlyList<PluginManagedFileStatus> Files)
{
    public bool AllReady => Files.Count > 0 && Files.All(file => file.Ready);

    /// <summary>Declarations that are absent or fail verification right now.</summary>
    public int UnreadyCount => Files.Count(file => !file.Ready);
}

/// <summary>
/// Optional desktop confirmation boundary for <see cref="PluginManagedFileService.EnsureAsync(PluginDefinition, CancellationToken)"/>.
/// The handler owns the confirmation UX (dialog, modal progress, cancellation); the supplied
/// <paramref name="download"/> delegate is the service's own verified, atomic download — the
/// handler must call it to produce files and must return its result (or decline/cancel).
/// </summary>
public interface IPluginManagedFileInteraction
{
    Task<Result> ConfirmAndDownloadAsync(
        PluginManagedFilesPlan plan,
        Func<IProgress<double>?, CancellationToken, Task<Result>> download,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-managed plugin files: computes missing/invalid declarations for a validated manifest
/// and performs confirmed, verified, atomic downloads. Destinations are always
/// <c>{filesRoot}/{sanitized pluginId}/{declared path}</c> — the manifest already proved the
/// declared relative path non-traversing, so resolution is a fixed join with no conditions,
/// scopes or custom overrides. All verification, transfer, single-flight and lifecycle work
/// is the shared <see cref="ManagedFileService"/> engine; this facade only owns manifest
/// resolution and the public plugin-typed contract. Transport is used only after
/// confirmation — status, planning and launch-argument composition never touch the network,
/// create directories, or run package code.
/// </summary>
public sealed class PluginManagedFileService : IDisposable, IAsyncDisposable
{
    private readonly string _filesRoot;
    private readonly ManagedFileService _engine;
    private bool _closed;

    /// <param name="filesRoot">
    /// Managed files root (host wires <c>{DataDirectory}/models/plugins</c>); each plugin gets a
    /// sanitized id directory below it.
    /// </param>
    public PluginManagedFileService(string filesRoot, HttpMessageInvoker? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filesRoot);
        _filesRoot = Path.GetFullPath(filesRoot);
        _engine = new ManagedFileService(http);
    }

    /// <summary>Optional desktop interaction; when unset, missing files report an actionable failure — never an automatic download.</summary>
    public IPluginManagedFileInteraction? Interaction { get; set; }

    /// <summary>The managed root directory for one plugin: <c>{filesRoot}/{sanitized id}</c>.</summary>
    public string ManagedRootFor(string pluginId)
    {
        return Path.Combine(_filesRoot, OcrStorageLocations.SanitizePathSegment(pluginId));
    }

    /// <summary>
    /// The canonical destination of one declared managed file. Manifest validation already
    /// rejected traversal, so the '/'-separated declaration is simply joined onto the
    /// plugin's managed root with OS separators.
    /// </summary>
    public string DestinationFor(string pluginId, string relativePath)
    {
        return Path.GetFullPath(Path.Combine(ManagedRootFor(pluginId),
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// The actually missing/invalid declared files for one validated manifest. Reads file
    /// length and digest only; never creates directories or touches the network.
    /// </summary>
    public async Task<PluginManagedFilesPlan> PlanAsync(PluginDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(definition);
        ManagedFilesPlan plan = await _engine.PlanAsync(OwnerKey(definition.Id), definition.Name,
                definition.ManagedFiles.Select(file => new ManagedFileTarget(file.Path,
                    file.DownloadUrl, DestinationFor(definition.Id, file.Path), file.ExpectedBytes,
                    file.Sha256, file.Version)), cancellationToken)
            .ConfigureAwait(false);
        return ToTyped(definition, plan);
    }

    /// <summary>
    /// Public per-declaration status: exists/bytes plus the verification booleans, never a local
    /// path. Synchronous metadata + bounded digest reads for the static fetch surface.
    /// </summary>
    public PluginManagedFileStatus DescribeStatus(PluginManagedFile file, string destination)
    {
        FileInfo info = new(destination);
        bool exists;
        long? bytes;
        try
        {
            exists = info.Exists;
            bytes = exists ? info.Length : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            exists = false;
            bytes = null;
        }

        bool ready = exists &&
                     ManagedFileService.IsSatisfiedSync(destination, file.ExpectedBytes, file.Sha256);
        return new PluginManagedFileStatus(file.Path, ready, exists, bytes, file.Sha256,
            file.ExpectedBytes, !exists, exists && !ready, file.DownloadUrl, file.Version);
    }

    /// <summary>Status for every declaration of one validated manifest.</summary>
    public IReadOnlyList<PluginManagedFileStatus> DescribeStatus(PluginDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.ManagedFiles
            .Select(file => DescribeStatus(file, DestinationFor(definition.Id, file.Path)))
            .ToArray();
    }

    /// <summary>
    /// The verified local path of one declared managed file — the only surface that exposes a
    /// filesystem location, reserved for the plugin's own UI bridge. Fails when the path is not
    /// declared or the file is absent/unverified; never creates directories.
    /// </summary>
    public Result<string> TryVerifiedLocalPath(PluginDefinition definition, string relativePath)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(definition);
        PluginManagedFile? file = definition.FindManagedFile(relativePath);
        if (file is null)
        {
            return Result<string>.Failure(AppErrorCodes.NotFound,
                $"'{relativePath}' is not a declared managed file of plugin '{definition.Id}'.");
        }

        string destination = DestinationFor(definition.Id, file.Path);
        return ManagedFileService.IsSatisfiedSync(destination, file.ExpectedBytes, file.Sha256)
            ? Result<string>.Success(destination)
            : Result<string>.Failure(AppErrorCodes.NotFound,
                $"Managed file '{relativePath}' of plugin '{definition.Id}' is not downloaded " +
                "or fails verification.");
    }

    /// <summary>
    /// Ensures the manifest's declared files exist, asking the optional interaction once per
    /// required set. An empty plan succeeds; missing files without an interaction report an
    /// actionable resource-missing failure — no caller ever triggers an unconfirmed download.
    /// Concurrent ensures for the same required set share one confirmation+download; each
    /// caller's cancellation only withdraws that caller, and the shared operation is cancelled
    /// once no waiters remain. A successful interaction is verified against the same frozen
    /// definition before success is reported.
    /// </summary>
    public async Task<Result> EnsureAsync(PluginDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(definition);

        async Task<ManagedFilesPlan> Plan(CancellationToken token)
        {
            return ToNormalized(await PlanAsync(definition, token).ConfigureAwait(false));
        }

        IPluginManagedFileInteraction? interaction = Interaction;
        return await _engine.EnsureAsync(OwnerKey(definition.Id), Plan,
                interaction is null
                    ? null
                    : (normalized, download, token) => interaction.ConfirmAndDownloadAsync(
                        ToTyped(definition, normalized), download, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads each planned file sequentially under the shared engine's per-destination
    /// cross-process lock, verification and atomic install rules.
    /// </summary>
    public Task<Result> DownloadAsync(PluginManagedFilesPlan plan,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(plan);
        return _engine.DownloadAsync(ToNormalized(plan), progress, cancellationToken);
    }

    internal static string OwnerKey(string pluginId)
    {
        return $"plugin:{pluginId}";
    }

    public static ManagedFileDownload ToNormalized(PluginManagedFileDownload file)
    {
        return new ManagedFileDownload(file.RelativePath, file.DownloadUrl, file.DestinationPath,
            file.ExpectedBytes, file.Sha256, file.Version)
        {
            OverwritesExisting = file.OverwritesExisting,
            ExistingLength = file.ExistingLength,
            ExistingWriteUtcTicks = file.ExistingWriteUtcTicks,
            ExistingSha256 = file.ExistingSha256
        };
    }

    public static PluginManagedFileDownload ToTyped(ManagedFileDownload file)
    {
        return new PluginManagedFileDownload(file.RelativePath, file.DownloadUrl,
            file.DestinationPath, file.ExpectedBytes, file.Sha256, file.Version)
        {
            OverwritesExisting = file.OverwritesExisting,
            ExistingLength = file.ExistingLength,
            ExistingWriteUtcTicks = file.ExistingWriteUtcTicks,
            ExistingSha256 = file.ExistingSha256
        };
    }

    public static ManagedFilesPlan ToNormalized(PluginManagedFilesPlan plan)
    {
        return new ManagedFilesPlan(OwnerKey(plan.PluginId), plan.DisplayName,
            plan.Files.Select(ToNormalized).ToArray())
        {
            RequiredFiles = plan.RequiredFiles.Select(ToNormalized).ToArray()
        };
    }

    private static PluginManagedFilesPlan ToTyped(PluginDefinition definition, ManagedFilesPlan plan)
    {
        return new PluginManagedFilesPlan(definition.Id, definition.Name,
            plan.Files.Select(ToTyped).ToArray())
        {
            RequiredFiles = plan.RequiredFiles.Select(ToTyped).ToArray()
        };
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed), this);
    }

    /// <summary>
    /// Requests shutdown without blocking: no new operations are admitted and every in-flight
    /// operation observes cancellation on the shared engine.
    /// </summary>
    public void Dispose()
    {
        _closed = true;
        _engine.Dispose();
    }

    /// <summary>
    /// Closes the service, cancels all admitted operations and waits for their real network/file
    /// work to exit before the owned transport is released by the shared engine.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _closed = true;
        await _engine.DisposeAsync().ConfigureAwait(false);
    }
}
