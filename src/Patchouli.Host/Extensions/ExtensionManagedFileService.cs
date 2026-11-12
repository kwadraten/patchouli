using System.Net;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.ManagedFiles;
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
/// All verification, transfer, single-flight and lifecycle work is the shared
/// <see cref="ManagedFileService"/> engine; this facade only owns FSX declaration→destination
/// resolution and the public extension-typed contract.
/// </summary>
public sealed class ExtensionManagedFileService : IDisposable, IAsyncDisposable
{
    private readonly string _modelsRoot;
    private readonly Func<string, string?>? _directoryResolver;
    private readonly ManagedFileService _engine;
    private bool _closed;

    public ExtensionManagedFileService(string modelsRoot,
        Func<string, string?>? legacyDirectoryResolver = null,
        HttpMessageInvoker? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        _modelsRoot = Path.GetFullPath(modelsRoot);
        _directoryResolver = legacyDirectoryResolver;
        _engine = new ManagedFileService(http);
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
        List<ManagedFileTarget> targets = [];
        HashSet<string> destinations = new(ManagedFileService.PathComparer());
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

                targets.Add(new ManagedFileTarget(file.RelativePath, file.DownloadUrl,
                    destination, file.ExpectedBytes, file.Sha256));
            }
        }

        ManagedFilesPlan plan = await _engine
            .PlanAsync(OwnerKey(extensionId, scope), extensionId, targets, cancellationToken)
            .ConfigureAwait(false);
        return new ExtensionManagedFilesPlan(extensionId, extensionId, resolved,
            plan.Files.Select(ToTyped).ToArray())
        {
            RequiredFiles = plan.RequiredFiles.Select(ToTyped).ToArray()
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
        ExtensionManagedFilesPlan? planned = null;

        async Task<ManagedFilesPlan> Plan(CancellationToken token)
        {
            planned = await PlanAsync(extensionId, fields, config, scope, token)
                .ConfigureAwait(false);
            return ToNormalized(planned, scope);
        }

        IExtensionManagedFileInteraction? interaction = Interaction;
        return await _engine.EnsureAsync(OwnerKey(extensionId, scope), Plan,
                interaction is null
                    ? null
                    : (normalized, download, token) => interaction.ConfirmAndDownloadAsync(
                        Rebind(normalized, planned ?? throw new InvalidOperationException(
                            "The ensure interaction ran before planning completed.")), download,
                        token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads each planned file sequentially under the shared engine's per-destination
    /// cross-process lock, verification and atomic install rules.
    /// </summary>
    public Task<Result> DownloadAsync(ExtensionManagedFilesPlan plan,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(plan);
        return _engine.DownloadAsync(ToNormalized(plan, null), progress, cancellationToken);
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
                             ManagedFileService.PathComparer()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ExtensionManagedFile File, string Destination)[] items = directory.ToArray();
                List<ExtensionManagedFileDownload> files = [];
                List<ExtensionManagedFileDownload> missing = [];
                foreach ((ExtensionManagedFile declared, string destination) in items)
                {
                    (ManagedFileDownload download, bool satisfied) =
                        await ManagedFileService.DescribeExistingAsync(
                            new ManagedFileTarget(declared.RelativePath, declared.DownloadUrl,
                                destination, declared.ExpectedBytes, declared.Sha256),
                            cancellationToken).ConfigureAwait(false);
                    files.Add(ToTyped(download));
                    if (!satisfied)
                    {
                        missing.Add(ToTyped(download));
                    }
                }

                groups.Add(new ExtensionManagedFileGroup(extensionId, field.Key, field.Label,
                    items.Select(item => item.File.DirectoryKind).FirstOrDefault(kind => kind is not null),
                    directory.Key, files.AsReadOnly(), missing.AsReadOnly()));
            }
        }

        return groups.AsReadOnly();
    }

    internal static string OwnerKey(string extensionId, string? scope)
    {
        return $"extension:{extensionId}\n{scope ?? ""}";
    }

    public static ManagedFileDownload ToNormalized(ExtensionManagedFileDownload file)
    {
        return new ManagedFileDownload(file.RelativePath, file.DownloadUrl, file.DestinationPath,
            file.ExpectedBytes, file.Sha256)
        {
            OverwritesExisting = file.OverwritesExisting,
            ExistingLength = file.ExistingLength,
            ExistingWriteUtcTicks = file.ExistingWriteUtcTicks,
            ExistingSha256 = file.ExistingSha256
        };
    }

    public static ExtensionManagedFileDownload ToTyped(ManagedFileDownload file)
    {
        return new ExtensionManagedFileDownload(file.RelativePath, file.DownloadUrl!,
            file.DestinationPath, file.ExpectedBytes, file.Sha256)
        {
            OverwritesExisting = file.OverwritesExisting,
            ExistingLength = file.ExistingLength,
            ExistingWriteUtcTicks = file.ExistingWriteUtcTicks,
            ExistingSha256 = file.ExistingSha256
        };
    }

    public static ManagedFilesPlan ToNormalized(ExtensionManagedFilesPlan plan, string? scope)
    {
        return new ManagedFilesPlan(OwnerKey(plan.ExtensionId, scope), plan.DisplayName,
            plan.Files.Select(ToNormalized).ToArray())
        {
            RequiredFiles = plan.RequiredFiles.Select(ToNormalized).ToArray()
        };
    }

    /// <summary>Rebinds a normalized ensure-plan onto the typed plan's captured resolved config.</summary>
    private static ExtensionManagedFilesPlan Rebind(ManagedFilesPlan plan,
        ExtensionManagedFilesPlan typed)
    {
        return new ExtensionManagedFilesPlan(typed.ExtensionId, typed.DisplayName,
            typed.ResolvedConfig, plan.Files.Select(ToTyped).ToArray())
        {
            RequiredFiles = plan.RequiredFiles.Select(ToTyped).ToArray()
        };
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
