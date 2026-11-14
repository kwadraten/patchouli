using Patchouli.Core.Diagnostics;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Host.ManagedFiles;
using Patchouli.Host.Plugins;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Rendering;
using Patchouli.Infrastructure.Translation;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Host.Composition;

/// <summary>
/// One unified managed-file directory group for the Local Files inventory: every declaration —
/// FSX extension managed defaults/custom overrides and plugin <c>[[files]]</c> declarations —
/// contributing files into the same canonical directory, plus the subset currently missing or
/// failing verification. Stable <see cref="Directory"/> identity; <see cref="DirectoryKind"/> is
/// cosmetic FSX display metadata. Download dispatch is internal: the UI sees only the common
/// normalized plan and download callback produced by
/// <see cref="ManagedFileInventoryService.PrepareDownloadAsync"/>.
/// </summary>
public sealed record ManagedInventoryGroup(
    string Name,
    string? DirectoryKind,
    string Directory,
    IReadOnlyList<ManagedFileDownload> Files,
    IReadOnlyList<ManagedFileDownload> Missing)
{
    public bool IsComplete => Missing.Count == 0;

    /// <summary>Total declared size across the group's missing files when byte counts are pinned.</summary>
    public long TotalExpectedBytes => Missing.Sum(file => file.ExpectedBytes ?? 0);

    /// <summary>Download dispatch metadata — replan + execute per contributing owner.</summary>
    internal IReadOnlyList<ManagedInventoryContribution> Contributions { get; init; } = [];
}

/// <summary>
/// One owner's contribution to a merged inventory group: replans the declarations fresh (a stale
/// snapshot never authorizes a download) and executes confirmed files through that owner's own
/// transport and shared engine.
/// </summary>
internal sealed record ManagedInventoryContribution(
    Func<CancellationToken, Task<ManagedFilesPlan>> Plan,
    Func<IReadOnlyList<ManagedFileDownload>, IProgress<double>?, CancellationToken, Task<Result>>
        Download);

/// <summary>
/// A confirmed download prepared from a fresh inventory read: the merged normalized plan shown
/// for confirmation and the callback that executes it through each contributing owner's engine.
/// </summary>
public sealed record ManagedInventoryDownload(
    ManagedFilesPlan Plan,
    Func<IProgress<double>?, CancellationToken, Task<Result>> Download);

/// <summary>The Local Files row for the page-render disk cache (issue #57).</summary>
public sealed record PageRenderCacheDescription(
    string Directory,
    int FileCount,
    long TotalBytes,
    long DiskLimitBytes,
    int PngQuality);

/// <summary>
/// Unified managed-file inventory: enumerates FSX extension declaration groups and plugin
/// <c>[[files]]</c> declaration groups, merges them by canonical directory and prepares
/// confirmed downloads that execute through each source's own
/// <see cref="ManagedFileService"/> engine. Reading the inventory is declaration + disk
/// metadata only — no network, no plugin processes, no directory creation.
/// </summary>
public sealed class ManagedFileInventoryService
{
    // Cosmetic ordering only — keeps the familiar row order for the known managed-directory
    // aliases; unknown kinds and plugin/fallback directories sort after, by path. This is
    // display metadata, never an engine-id switch: rows and downloads come from declarations.
    private static readonly string[] PreferredDirectoryKindOrder =
    [
        "doclayout-yolo-models", "pp-doclayout-v3-models",
        "ndl-koten-models", "ndlocr-lite-models", "rapidocr-models"
    ];

    private readonly TranslationExtensionCatalog _extensions;
    private readonly ExtensionManagedFileService _extensionFiles;
    private readonly OcrStorageLocations _storage;
    private readonly IPluginManagedDefinitionSource? _plugins;
    private readonly PluginManagedFileService _pluginFiles;
    private readonly PageRenderService? _pageRenders;

    public ManagedFileInventoryService(TranslationExtensionCatalog extensions,
        ExtensionManagedFileService extensionFiles, OcrStorageLocations storage,
        IPluginManagedDefinitionSource? plugins, PluginManagedFileService pluginFiles,
        PageRenderService? pageRenders = null)
    {
        _extensions = extensions;
        _extensionFiles = extensionFiles;
        _storage = storage;
        _plugins = plugins;
        _pluginFiles = pluginFiles;
        _pageRenders = pageRenders;
    }

    /// <summary>
    /// The page-render cache row for Local Files management (issue #57): live disk usage plus
    /// the configured limit/quality the UI displays beside it.
    /// </summary>
    public async Task<Result<PageRenderCacheDescription>> DescribePageRenderCacheAsync(
        CancellationToken cancellationToken = default)
    {
        if (_pageRenders is null)
        {
            return Result<PageRenderCacheDescription>.Failure(AppErrorCodes.UnsupportedOperation,
                "Page render cache is not configured for this host.");
        }

        PageRenderCacheUsage usage = await _pageRenders.GetCacheUsageAsync();
        return Result<PageRenderCacheDescription>.Success(new PageRenderCacheDescription(
            usage.CacheRoot, usage.FileCount, usage.TotalBytes, _pageRenders.DiskCacheLimitBytes,
            _pageRenders.PngQuality));
    }

    /// <summary>Clears every cached page render; renders are rebuilt lazily on demand.</summary>
    public async Task<Result> ClearPageRenderCacheAsync(CancellationToken cancellationToken = default)
    {
        if (_pageRenders is null)
        {
            return Result.Failure(AppErrorCodes.UnsupportedOperation,
                "Page render cache is not configured for this host.");
        }

        try
        {
            await _pageRenders.ClearDiskCacheAsync();
            return Result.Success();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "host.managed-files", "clear-page-render-cache"))
        {
            return Result.Failure(AppErrorCodes.InvalidState, $"Clearing the render cache failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Every declared managed-file directory group, merged across extensions and plugins by
    /// canonical destination directory so two packages sharing a location produce one row.
    /// </summary>
    public async Task<IReadOnlyList<ManagedInventoryGroup>> DescribeAsync(
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, MutableGroup> merged = new(ManagedFileService.PathComparer());
        List<string> order = [];

        MutableGroup GetOrCreate(string directory, string name, string? directoryKind)
        {
            string key = Path.GetFullPath(directory);
            if (!merged.TryGetValue(key, out MutableGroup? target))
            {
                target = new MutableGroup(name, directoryKind, directory);
                merged[key] = target;
                order.Add(key);
            }

            return target;
        }

        await CollectExtensionGroupsAsync(GetOrCreate, cancellationToken).ConfigureAwait(false);
        await CollectPluginGroupsAsync(GetOrCreate, cancellationToken).ConfigureAwait(false);

        return order.OrderBy(directory =>
            {
                MutableGroup group = merged[directory];
                int rank = group.DirectoryKind is { } kind
                    ? Array.FindIndex(PreferredDirectoryKindOrder,
                        candidate => string.Equals(candidate, kind,
                            StringComparison.OrdinalIgnoreCase))
                    : -1;
                return (rank < 0 ? PreferredDirectoryKindOrder.Length : rank, directory);
            }, Comparer<(int Rank, string Directory)>.Create(static (left, right) =>
            {
                int rank = left.Rank.CompareTo(right.Rank);
                return rank != 0
                    ? rank
                    : ManagedFileService.PathComparer().Compare(left.Directory, right.Directory);
            }))
            .Select(directory => merged[directory].ToGroup())
            .ToArray();
    }

    /// <summary>
    /// Re-reads the group's declarations and returns the confirmed download to run: a merged
    /// normalized plan of the currently missing/invalid files plus the callback executing each
    /// contributing owner through its own engine. A group that no longer exists is
    /// <see cref="AppErrorCodes.NotFound"/> — a stale row can never trigger a download.
    /// </summary>
    public async Task<Result<ManagedInventoryDownload>> PrepareDownloadAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string canonical = Path.GetFullPath(directory);
        ManagedInventoryGroup? group = (await DescribeAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(Path.GetFullPath(candidate.Directory),
                canonical, ManagedFileService.PathComparison()));
        if (group is null)
        {
            return Result<ManagedInventoryDownload>.Failure(AppErrorCodes.NotFound,
                $"The managed-file group '{directory}' no longer exists; refresh the inventory.");
        }

        return await PrepareDownloadAsync(group, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Test seam: prepares the confirmed download for a previously described group.</summary>
    internal Task<Result<ManagedInventoryDownload>> PrepareDownloadAsync(
        ManagedInventoryGroup group, CancellationToken cancellationToken = default)
    {
        return PrepareDownloadCoreAsync(group, cancellationToken);
    }

    private static async Task<Result<ManagedInventoryDownload>> PrepareDownloadCoreAsync(
        ManagedInventoryGroup group, CancellationToken cancellationToken)
    {
        string canonical = Path.GetFullPath(group.Directory);
        List<(ManagedInventoryContribution Contribution, ManagedFilesPlan Plan)> parts = [];
        foreach (ManagedInventoryContribution contribution in group.Contributions)
        {
            parts.Add((contribution, await contribution.Plan(cancellationToken)
                .ConfigureAwait(false)));
        }

        // Validate ALL required declarations — the satisfied ones included. Two declarations
        // for one destination must agree on content (URL/bytes/digest) even when one owner is
        // already satisfied: a silent overwrite by a declaration the prompt never showed would
        // defeat confirmation, so differing duplicates are a Conflict before anything is
        // downloaded. RequiredFiles is the immutable full declaration set; hand-built plans
        // fall back to Files.
        Dictionary<string, ManagedFileDownload> declared = new(ManagedFileService.PathComparer());
        for (int index = 0; index < parts.Count; index++)
        {
            ManagedFilesPlan plan = parts[index].Plan;
            IEnumerable<ManagedFileDownload> required =
                plan.RequiredFiles.Count > 0 ? plan.RequiredFiles : plan.Files;
            foreach (ManagedFileDownload file in required)
            {
                string destination = Path.GetFullPath(file.DestinationPath);
                if (declared.TryGetValue(destination, out ManagedFileDownload? existing) &&
                    !SameDeclaration(existing, file))
                {
                    return Result<ManagedInventoryDownload>.Failure(AppErrorCodes.Conflict,
                        $"Two declarations target '{file.RelativePath}' with different " +
                        "download requirements; resolve the conflicting declarations " +
                        "before downloading.");
                }

                declared.TryAdd(destination, file);
            }
        }

        // Merge the freshly planned missing sets by canonical destination and assign each file
        // to its first declaring owner. Identical duplicates dedupe and run once.
        List<ManagedFileDownload> missing = [];
        Dictionary<string, (int Owner, ManagedFileDownload File)> assigned =
            new(ManagedFileService.PathComparer());
        for (int index = 0; index < parts.Count; index++)
        {
            foreach (ManagedFileDownload file in parts[index].Plan.Files)
            {
                string destination = Path.GetFullPath(file.DestinationPath);
                if (assigned.TryGetValue(destination, out (int Owner, ManagedFileDownload File) _))
                {
                    continue;
                }

                assigned[destination] = (index, file);
                missing.Add(file);
            }
        }

        // Execution consumes exactly the deduped set the confirmation shows — never an
        // unfiltered per-owner file list.
        ManagedFilesPlan combined = new($"inventory:{canonical}", group.Name, missing.AsReadOnly());
        return Result<ManagedInventoryDownload>.Success(new ManagedInventoryDownload(combined,
            async (progress, token) =>
            {
                // Scalar progress aggregates across owner batches, weighted by pinned byte
                // counts (one unit otherwise), and never rewinds when ownership changes.
                double totalWeight = missing.Sum(static file => WeightOf(file));
                double completedWeight = 0;
                double floor = 0;
                foreach (IGrouping<int, (ManagedFileDownload file, int position)> batch in missing
                             .Select((file, position) => (file, position))
                             .GroupBy(pair => assigned[Path.GetFullPath(pair.file.DestinationPath)].Owner)
                             .OrderBy(grouping => grouping.Min(pair => pair.position)))
                {
                    double weight = batch.Sum(pair => WeightOf(pair.file));
                    double baseWeight = completedWeight;
                    IProgress<double>? scaled = progress is null
                        ? null
                        : new ScaledProgress(progress, baseWeight, weight, totalWeight,
                            () => floor, value => floor = value);
                    ManagedInventoryContribution contribution =
                        parts[batch.Key].Contribution;
                    Result result = await contribution.Download(
                            batch.Select(pair => pair.file).ToArray(), scaled, token)
                        .ConfigureAwait(false);
                    if (result.IsFailure)
                    {
                        return result;
                    }

                    completedWeight += weight;
                    progress?.Report(Math.Min(1, completedWeight / totalWeight));
                }

                progress?.Report(1);
                return Result.Success();
            }));
    }

    /// <summary>Progress weight: pinned byte count when declared, one unit otherwise.</summary>
    private static double WeightOf(ManagedFileDownload file)
    {
        return file.ExpectedBytes is > 0 ? file.ExpectedBytes.Value : 1;
    }

    /// <summary>Content identity for duplicate-destination declarations.</summary>
    private static bool SameDeclaration(ManagedFileDownload first, ManagedFileDownload second)
    {
        return string.Equals(first.DownloadUrl, second.DownloadUrl, StringComparison.Ordinal) &&
               first.ExpectedBytes == second.ExpectedBytes &&
               string.Equals(first.Sha256, second.Sha256,
                   StringComparison.OrdinalIgnoreCase); // hex digests compare case-free
    }

    /// <summary>
    /// Scales one owner batch's scalar progress into the shared aggregate while forwarding the
    /// rich per-file channel untouched.
    /// </summary>
    private sealed class ScaledProgress(
        IProgress<double> inner,
        double offset,
        double weight,
        double total,
        Func<double> floor,
        Action<double> raiseFloor)
        : IProgress<double>, IDownloadProgress
    {
        public void Report(double value)
        {
            double scaled = Math.Min(1, (offset + weight * Math.Clamp(value, 0, 1)) / total);
            double aggregate = Math.Max(floor(), scaled);
            raiseFloor(aggregate);
            inner.Report(aggregate);
        }

        public void ReportFile(DownloadFileProgress value)
        {
            (inner as IDownloadProgress)?.ReportFile(value);
        }
    }

    /// <summary>
    /// FSX extension groups: every trusted candidate package's declared directory groups —
    /// managed defaults keep all variants; a custom override keeps the selected variant only.
    /// Failures of a single package never hide the others.
    /// </summary>
    private async Task CollectExtensionGroupsAsync(
        Func<string, string, string?, MutableGroup> getOrCreate,
        CancellationToken cancellationToken)
    {
        foreach (string extensionId in CandidateExtensionIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtensionConfigurationDescription description;
            try
            {
                description = await _extensions
                    .GetConfigurationDescriptionAsync(extensionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue;
            }

            if (!description.HasTrustedSchema)
            {
                continue;
            }

            ConfigFieldDescriptor[] managedFields = description.Fields
                .Where(field => field.ManagedFiles is { Count: > 0 })
                .ToArray();
            if (managedFields.Length == 0)
            {
                continue;
            }

            ExtensionConfigSnapshot config;
            try
            {
                config = await _extensions.Manager
                    .ReadConfigAsync(extensionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                config = EmptyConfig();
            }

            IReadOnlyList<ExtensionManagedFileGroup> groups;
            ExtensionConfigSnapshot resolvedConfig;
            try
            {
                resolvedConfig = _extensionFiles.ResolveDefaults(extensionId, managedFields, config);
                groups = await _extensionFiles
                    .DescribeGroupsAsync(extensionId, managedFields, config, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue;
            }

            foreach (ManagedInventoryGroup built in BuildExtensionGroups(extensionId,
                         description.Name, managedFields, config, resolvedConfig, groups))
            {
                MutableGroup target = getOrCreate(built.Directory, built.Name, built.DirectoryKind);
                target.Merge(built.Files, built.Missing);
                target.Contributions.AddRange(built.Contributions);
            }
        }
    }

    /// <summary>
    /// One extension's declaration groups folded into unified inventory groups: fields sharing a
    /// canonical destination directory merge into a single row AND a single contribution whose
    /// plan covers every field's missing set in that directory. (Splitting contributions per
    /// field while matching only by directory replans every merged field onto the first — the
    /// later fields would render in the row yet never download.)
    /// Internal so focused tests can exercise the real contribution planning without the
    /// catalog/discovery scaffolding.
    /// </summary>
    internal IReadOnlyList<ManagedInventoryGroup> BuildExtensionGroups(string extensionId,
        string displayName, IReadOnlyList<ConfigFieldDescriptor> fields,
        ExtensionConfigSnapshot config, ExtensionConfigSnapshot resolvedConfig,
        IReadOnlyList<ExtensionManagedFileGroup> groups)
    {
        List<ManagedInventoryGroup> result = [];
        foreach (IGrouping<string, ExtensionManagedFileGroup> directory in groups.GroupBy(
                     group => Path.GetFullPath(group.Directory), ManagedFileService.PathComparer()))
        {
            ExtensionManagedFileGroup[] members = directory.ToArray();
            string name = _storage.ModelDirectoryDisplayNameFor(members[0].DirectoryKind) ??
                          $"{displayName} {members[0].FieldLabel}";
            ManagedFileDownload[] files = Dedup(members.SelectMany(group => group.Files));
            ManagedFileDownload[] missing = Dedup(members.SelectMany(group => group.Missing));
            string canonicalDirectory = directory.Key;
            result.Add(new ManagedInventoryGroup(name, members[0].DirectoryKind,
                canonicalDirectory, files, missing)
            {
                Contributions =
                [
                    new ManagedInventoryContribution(
                        async token =>
                        {
                            IReadOnlyList<ExtensionManagedFileGroup> fresh = await _extensionFiles
                                .DescribeGroupsAsync(extensionId, fields, config, token)
                                .ConfigureAwait(false);
                            ExtensionManagedFileGroup[] current = fresh.Where(candidate =>
                                    string.Equals(Path.GetFullPath(candidate.Directory),
                                        canonicalDirectory, ManagedFileService.PathComparison()))
                                .ToArray();
                            return new ManagedFilesPlan(
                                ExtensionManagedFileService.OwnerKey(extensionId, null), name,
                                Dedup(current.SelectMany(group => group.Missing)))
                            {
                                // Raw declaration candidates — never deduped here: differing
                                // same-destination declarations inside one extension must reach
                                // the conflict validator instead of being silently discarded.
                                RequiredFiles = current.SelectMany(group => group.Files)
                                    .Select(ExtensionManagedFileService.ToNormalized).ToArray()
                            };
                        },
                        (downloadFiles, progress, token) => _extensionFiles.DownloadAsync(
                            new ExtensionManagedFilesPlan(extensionId, name, resolvedConfig,
                                downloadFiles.Select(ExtensionManagedFileService.ToTyped)
                                    .ToArray()),
                            progress, token))
                ]
            });
        }

        return result;
    }

    /// <summary>Destination-deduped normalized downloads, first declaration wins.</summary>
    private static ManagedFileDownload[] Dedup(IEnumerable<ExtensionManagedFileDownload> files)
    {
        List<ManagedFileDownload> deduped = [];
        HashSet<string> seen = new(ManagedFileService.PathComparer());
        foreach (ExtensionManagedFileDownload file in files)
        {
            if (seen.Add(Path.GetFullPath(file.DestinationPath)))
            {
                deduped.Add(ExtensionManagedFileService.ToNormalized(file));
            }
        }

        return deduped.ToArray();
    }

    /// <summary>
    /// Plugin groups: one row per mounted plugin declaring managed files, at the plugin's
    /// managed root — nested declared paths stay in the single row. Mounts whose process is
    /// not running (or holds no verb grants) still list: managing host files is not execution.
    /// </summary>
    private async Task CollectPluginGroupsAsync(
        Func<string, string, string?, MutableGroup> getOrCreate,
        CancellationToken cancellationToken)
    {
        if (_plugins is not { } api)
        {
            return;
        }

        foreach (PluginDefinition definition in api.ManagedFileDefinitions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = _pluginFiles.ManagedRootFor(definition.Id);
            ManagedFilesPlan plan;
            try
            {
                plan = PluginManagedFileService.ToNormalized(
                    await _pluginFiles.PlanAsync(definition, cancellationToken)
                        .ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue;
            }

            string name = $"{definition.Name} 插件文件";
            string pluginId = definition.Id;
            MutableGroup target = getOrCreate(root, name, null);
            target.Merge(plan.RequiredFiles, plan.Files);
            target.Contributions.Add(new ManagedInventoryContribution(
                async token =>
                {
                    PluginDefinition? current = api.ManagedFileDefinitions()
                        .FirstOrDefault(candidate => string.Equals(candidate.Id, pluginId,
                            StringComparison.Ordinal));
                    return current is null
                        ? new ManagedFilesPlan(PluginManagedFileService.OwnerKey(pluginId), name, [])
                        : PluginManagedFileService.ToNormalized(
                            await _pluginFiles.PlanAsync(current, token).ConfigureAwait(false));
                },
                (files, progress, token) => _pluginFiles.DownloadAsync(
                    new PluginManagedFilesPlan(pluginId, name,
                        files.Select(PluginManagedFileService.ToTyped).ToArray()),
                    progress, token)));
        }
    }

    private IEnumerable<string> CandidateExtensionIds()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in BuiltInOcrExtensions.PackageIds
                     .Concat(BuiltInPdfTextLayerExtensions.Packages.Select(package => package.Id))
                     .Concat(BuiltInLayoutRecognitionExtensions.Packages.Select(package => package.Id))
                     .Concat(BuiltInLayoutRecognitionExtensions.Packages.Select(package => package.Id))
                     .Concat(BuiltInTranslationExtensions.Packages.Select(package => package.Id))
                     .Concat(BuiltInMetadataExtensions.Packages.Select(package => package.Id))
                     .Concat(_extensions.ListWorkingPackages())
                     .Concat(_extensions.Manager.List().Select(info => info.Id)))
        {
            if (seen.Add(id))
            {
                yield return id;
            }
        }
    }

    private static ExtensionConfigSnapshot EmptyConfig()
    {
        return new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0);
    }

    /// <summary>The mutable merge accumulator behind the immutable <see cref="ManagedInventoryGroup"/>.</summary>
    private sealed class MutableGroup(string name, string? directoryKind, string directory)
    {
        private readonly List<ManagedFileDownload> _files = [];
        private readonly List<ManagedFileDownload> _missing = [];

        public string? DirectoryKind { get; } = directoryKind;

        public List<ManagedInventoryContribution> Contributions { get; } = [];

        public void Merge(IEnumerable<ManagedFileDownload> files,
            IEnumerable<ManagedFileDownload> missing)
        {
            foreach (ManagedFileDownload file in files)
            {
                if (_files.All(existing => !string.Equals(existing.DestinationPath,
                        file.DestinationPath, ManagedFileService.PathComparison())))
                {
                    _files.Add(file);
                }
            }

            foreach (ManagedFileDownload file in missing)
            {
                if (_missing.All(existing => !string.Equals(existing.DestinationPath,
                        file.DestinationPath, ManagedFileService.PathComparison())))
                {
                    _missing.Add(file);
                }
            }
        }

        public ManagedInventoryGroup ToGroup()
        {
            return new ManagedInventoryGroup(name, DirectoryKind, directory,
                _files.AsReadOnly(), _missing.AsReadOnly())
            {
                Contributions = Contributions.AsReadOnly()
            };
        }
    }
}
