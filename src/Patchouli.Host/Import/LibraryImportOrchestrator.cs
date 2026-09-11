using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Credentials;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Ocr;

namespace Patchouli.Host.Import;

/// <summary>
/// Host-side orchestration for library import flows: the BibLaTeX single/batch import
/// pipeline (parse → map/preview → conflict resolution → apply), the first-run PDF
/// scan/import workflow, and batch OCR enqueue. UI concerns (file picking, dialogs,
/// status reporting) are injected as prompt callbacks or left to the caller.
/// </summary>
public sealed class LibraryImportOrchestrator
{
    public const string MinerUTokenRequiredErrorCode = "mineru_token_required";
    public const string ImportPromptUnavailableErrorCode = "import_prompt_unavailable";

    private readonly HostServices _services;
    private readonly IImportConflictPrompt? _conflictPrompt;
    private readonly IBiblatexImportPrompt? _importPrompt;

    public LibraryImportOrchestrator(
        HostServices services,
        IImportConflictPrompt? conflictPrompt = null,
        IBiblatexImportPrompt? importPrompt = null)
    {
        _services = services;
        _conflictPrompt = conflictPrompt;
        _importPrompt = importPrompt;
    }

    /// <summary>
    /// Runs the batch BibLaTeX import pipeline for a .bib file chosen by the caller:
    /// parse file → batch preview → link-conflict resolution (or silent-create
    /// confirmation) → apply batch. A null <see cref="BiblatexImportApplyResult"/> value
    /// means the user cancelled at a prompt.
    /// </summary>
    public async Task<Result<BiblatexImportApplyResult?>> ImportBiblatexFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<BiblatexEntryDto>> parsed =
            await _services.BiblatexImport.ParseFileAsync(path, cancellationToken);
        if (parsed.IsFailure)
        {
            return Result<BiblatexImportApplyResult?>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }

        Result<BiblatexBatchImportPreview> preview =
            await _services.BiblatexImport.PreviewBatchAsync(parsed.Value, cancellationToken);
        if (preview.IsFailure)
        {
            return Result<BiblatexImportApplyResult?>.Failure(preview.ErrorCode!, preview.ErrorMessage!);
        }

        IReadOnlyDictionary<string, string>? linkChoices = null;
        if (preview.Value.Plan.LinkConflictDescriptor is { } linkConflict)
        {
            linkChoices = await ResolveConflictChoicesAsync(linkConflict, cancellationToken);
            if (linkChoices is null)
            {
                return Result<BiblatexImportApplyResult?>.Success(null);
            }
        }
        else
        {
            if (_importPrompt is null)
            {
                return PromptUnavailable();
            }

            BiblatexBatchConfirmRequest confirm = new(
                preview.Value.Plan.Groups.Select(static group => group.Source).ToArray(),
                $"将静默新建 {preview.Value.Plan.Groups.Count} 条题录（无关联候选）。");
            bool confirmed = await _importPrompt.ConfirmSilentBatchCreateAsync(confirm, cancellationToken);
            if (!confirmed)
            {
                return Result<BiblatexImportApplyResult?>.Success(null);
            }
        }

        string? directory = Path.GetDirectoryName(path);
        Result<BiblatexImportApplyResult> applied = await _services.BiblatexImport.ApplyBatchAsync(
            preview.Value.Plan,
            linkChoices,
            directory,
            cancellationToken);
        return applied.IsFailure
            ? Result<BiblatexImportApplyResult?>.Failure(applied.ErrorCode!, applied.ErrorMessage!)
            : Result<BiblatexImportApplyResult?>.Success(applied.Value);
    }

    /// <summary>
    /// Runs the single-entry BibLaTeX import pipeline for parsed text (e.g. dropped into
    /// the item editor): parse text → map visible entries → entry pick (when multiple) →
    /// single preview → field-conflict resolution → apply single. A null
    /// <see cref="BiblatexImportApplyResult"/> value means the user cancelled at a prompt.
    /// </summary>
    public async Task<Result<BiblatexImportApplyResult?>> ImportBiblatexTextAsync(
        string text,
        string? bibFileDirectory,
        ItemId? targetItemId,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<BiblatexEntryDto>> parsed =
            await _services.BiblatexImport.ParseTextAsync(text, cancellationToken);
        if (parsed.IsFailure)
        {
            return Result<BiblatexImportApplyResult?>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }

        Result<IReadOnlyList<BiblatexMappedItem>> mapped = BiblatexImportPlanner.MapVisibleEntries(parsed.Value);
        if (mapped.IsFailure)
        {
            return Result<BiblatexImportApplyResult?>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
        }

        if (mapped.Value.Count == 0)
        {
            return Result<BiblatexImportApplyResult?>.Failure(
                AppErrorCodes.NotFound,
                "没有可导入的 BibLaTeX 条目（@xdata 不会创建题录）。");
        }

        BiblatexMappedItem source = mapped.Value[0];
        if (mapped.Value.Count > 1)
        {
            if (_importPrompt is null)
            {
                return PromptUnavailable();
            }

            BiblatexEntryPickRequest pick = new(
                mapped.Value,
                "源文件包含多条可见条目。请选择一条导入当前题录；批量导入请使用菜单「从 BibLaTeX 批量导入」。");
            string? selectedEntryKey = await _importPrompt.SelectEntryKeyAsync(pick, cancellationToken);
            if (string.IsNullOrWhiteSpace(selectedEntryKey))
            {
                return Result<BiblatexImportApplyResult?>.Success(null);
            }

            source = mapped.Value.Single(item =>
                string.Equals(item.SourceEntryKey, selectedEntryKey, StringComparison.Ordinal));
        }

        BiblatexEntryDto entry = parsed.Value.First(candidate =>
            string.Equals(candidate.Key, source.SourceEntryKey, StringComparison.Ordinal) && !candidate.IsXdata);
        Result<BiblatexSingleImportPreview> plan =
            await _services.BiblatexImport.PreviewSingleAsync(entry, targetItemId, cancellationToken);
        if (plan.IsFailure)
        {
            return Result<BiblatexImportApplyResult?>.Failure(plan.ErrorCode!, plan.ErrorMessage!);
        }

        IReadOnlyDictionary<string, string>? fieldChoices = null;
        if (plan.Value.FieldConflictDescriptor is { } fieldConflict)
        {
            fieldChoices = await ResolveConflictChoicesAsync(fieldConflict, cancellationToken);
            if (fieldChoices is null)
            {
                return Result<BiblatexImportApplyResult?>.Success(null);
            }
        }

        Result<BiblatexImportApplyResult> applied = await _services.BiblatexImport.ApplySingleAsync(
            plan.Value.Source,
            targetItemId,
            fieldChoices,
            bibFileDirectory,
            cancellationToken);
        return applied.IsFailure
            ? Result<BiblatexImportApplyResult?>.Failure(applied.ErrorCode!, applied.ErrorMessage!)
            : Result<BiblatexImportApplyResult?>.Success(applied.Value);
    }

    private async Task<IReadOnlyDictionary<string, string>?> ResolveConflictChoicesAsync(
        ConflictDescriptor conflict,
        CancellationToken cancellationToken)
    {
        if (_conflictPrompt is null)
        {
            return null;
        }

        ConflictPromptChoice? choice = await _conflictPrompt.ResolveAsync(conflict, cancellationToken);
        if (choice is null ||
            string.Equals(choice.ActionId, "leave_unresolved", StringComparison.Ordinal) ||
            choice.Choices is null)
        {
            return null;
        }

        Result<ConflictExecutionResult> executed = await _services.ConflictActions.ExecuteAsync(
            conflict,
            new ConflictActionSelection(choice.ActionId, choice.OptionId, choice.Choices),
            cancellationToken);
        return executed.IsSuccess ? choice.Choices : null;
    }

    private Result<BiblatexImportApplyResult?> PromptUnavailable()
    {
        return Result<BiblatexImportApplyResult?>.Failure(
            ImportPromptUnavailableErrorCode,
            "导入流程需要 UI 提示回调，但当前未注入。");
    }

    public Task<FirstRunWorkflowState> CreateFirstRunLibraryAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        return _services.FirstRunWorkflow.CreateLibraryAsync(displayName, cancellationToken);
    }

    /// <summary>Wraps <see cref="FirstRunWorkflow.ScanAndImportAsync"/> so callers do not
    /// need to reach into the workflow/discovery service pair directly.</summary>
    public Task<FirstRunImportResult> ScanDirectoryAndImportAsync(
        SelectedFileSearchRoot selectedRoot,
        string? libraryId,
        CancellationToken cancellationToken = default,
        Action<int?, int?, string, string?>? progress = null)
    {
        return _services.FirstRunWorkflow.ScanAndImportAsync(
            selectedRoot,
            libraryId,
            cancellationToken,
            progress);
    }

    /// <summary>Wraps <see cref="FirstRunWorkflow.ImportPdfAsync"/> for a single PDF chosen by the caller.</summary>
    public Task<FirstRunWorkflowState> ImportFirstRunPdfAsync(
        PdfImportRequest request,
        CancellationToken cancellationToken = default)
    {
        return _services.FirstRunWorkflow.ImportPdfAsync(request, cancellationToken);
    }

    /// <summary>True when the engine requires a MinerU API token before OCR can run.</summary>
    public static bool RequiresMinerUToken(string engineId, OcrEngineCapability capability)
    {
        return engineId == OcrEngineIds.MinerU && capability.RequiresCredential;
    }

    /// <summary>
    /// Ensures the default OCR preset exists for the engine, creating it on first use.
    /// Replaces the ViewModel helper that threw on failure; errors surface as Result failures.
    /// </summary>
    public async Task<Result<OcrPresetId>> EnsurePresetForEngineAsync(
        string engineId,
        CancellationToken cancellationToken = default)
    {
        return engineId switch
        {
            OcrEngineIds.MinerU => await EnsureMinerUPresetAsync(cancellationToken),
            OcrEngineIds.NdlKoten => await EnsureNdlKotenPresetAsync(cancellationToken),
            OcrEngineIds.NdlLite => await EnsureNdlLitePresetAsync(cancellationToken),
            OcrEngineIds.RapidOcr => await EnsureRapidOcrPresetAsync(cancellationToken),
            _ => Result<OcrPresetId>.Failure(
                AppErrorCodes.UnsupportedOperation,
                $"未实现默认 OCR preset 的引擎：{engineId}")
        };
    }

    /// <summary>
    /// Enqueues document OCR for the given items using the resolved engine: adapter lookup,
    /// MinerU token gating, default preset ensure, then one queue task per item. Items without
    /// a usable document source are counted as skipped; per-item failures are collected in the
    /// summary instead of being reported through dialogs.
    /// </summary>
    public async Task<Result<OcrEnqueueSummary>> EnqueueOcrForItemsAsync(
        IReadOnlyList<OcrEnqueueItem> items,
        string engineId,
        string priority,
        string? minerUTokenOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return Result<OcrEnqueueSummary>.Failure(AppErrorCodes.ValidationFailed, "请先选择题录。");
        }

        IRealOcrAdapter? adapter = _services.OcrAdapters.GetAdapter(engineId);
        if (adapter is null)
        {
            return Result<OcrEnqueueSummary>.Failure(
                AppErrorCodes.NotFound,
                $"未注册 OCR 引擎：{engineId}");
        }

        if (RequiresMinerUToken(engineId, adapter.GetCapability()))
        {
            string token = !string.IsNullOrWhiteSpace(minerUTokenOverride)
                ? minerUTokenOverride.Trim()
                : await ResolvePersistedMinerUTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return Result<OcrEnqueueSummary>.Failure(
                    MinerUTokenRequiredErrorCode,
                    "运行 OCR 前需要 MinerU API token。请先在设置中完成配置。");
            }
        }

        Result<OcrPresetId> preset = await EnsurePresetForEngineAsync(engineId, cancellationToken);
        if (preset.IsFailure)
        {
            return Result<OcrEnqueueSummary>.Failure(
                preset.ErrorCode ?? AppErrorCodes.InvalidState,
                $"OCR preset 不可用：{preset.ErrorMessage}");
        }

        int succeeded = 0;
        int failed = 0;
        int skipped = 0;
        List<OcrEnqueueFailure> failures = [];
        IOcrQueueScheduler? observedQueue = null;
        foreach (OcrEnqueueItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.DocumentInstanceId) || string.IsNullOrWhiteSpace(item.SourcePath))
            {
                skipped++;
                continue;
            }

            DocumentInstanceId documentInstanceId;
            try
            {
                documentInstanceId = DocumentInstanceId.Parse(item.DocumentInstanceId);
            }
            catch (FormatException)
            {
                failed++;
                failures.Add(new OcrEnqueueFailure(item.Title, "文档标识无效。"));
                continue;
            }

            Result<OcrQueueTask> queued = await QueueOcrForItemAsync(
                documentInstanceId,
                preset.Value,
                priority,
                cancellationToken);
            if (queued.IsSuccess)
            {
                succeeded++;
            }
            else
            {
                failed++;
                failures.Add(new OcrEnqueueFailure(
                    item.Title,
                    queued.ErrorMessage ?? "未知错误"));
            }
        }

        Result<IOcrQueueScheduler> queue = await _services.GetOcrQueueAsync(cancellationToken);
        if (queue.IsSuccess)
        {
            observedQueue = queue.Value;
        }

        return Result<OcrEnqueueSummary>.Success(
            new OcrEnqueueSummary(succeeded, failed, skipped, failures, observedQueue));
    }

    private async Task<string> ResolvePersistedMinerUTokenAsync(CancellationToken cancellationToken)
    {
        Result<string> secret =
            await _services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU, cancellationToken);
        return secret.IsSuccess ? secret.Value : "";
    }

    private async Task<Result<OcrQueueTask>> QueueOcrForItemAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        string priority,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<Page>> pages =
            await _services.Pages.ListPagesAsync(documentInstanceId, cancellationToken);
        if (pages.IsFailure)
        {
            return Result<OcrQueueTask>.Failure(pages.ErrorCode!, pages.ErrorMessage!);
        }

        PageId[] pageIds = pages.Value.Select(static page => page.PageId).ToArray();
        if (pageIds.Length == 0)
        {
            return Result<OcrQueueTask>.Failure(
                AppErrorCodes.ValidationFailed,
                "Document instance has no pages to OCR.");
        }

        Result<OcrPresetVersion> version =
            await _services.OcrPresets.GetCurrentVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrQueueTask>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        Result<IOcrQueueScheduler> queue = await _services.GetOcrQueueAsync(cancellationToken);
        if (queue.IsFailure)
        {
            return Result<OcrQueueTask>.Failure(queue.ErrorCode!, queue.ErrorMessage!);
        }

        string adapterKind = version.Value.EngineId == OcrEngineIds.MinerU
            ? OcrAdapterKind.CloudApi
            : OcrAdapterKind.LocalLibrary;
        string? providerId = version.Value.EngineId == OcrEngineIds.MinerU ? ProviderIds.MinerU : null;
        return await _services.Ocr.QueueDocumentOcrAsync(
            documentInstanceId,
            presetId,
            pageIds,
            version.Value.EngineId,
            adapterKind,
            providerId,
            priority,
            cancellationToken);
    }

    private async Task<Result<OcrPresetId>> EnsureMinerUPresetAsync(CancellationToken cancellationToken)
    {
        Result<OcrPreset?> existing =
            await _services.OcrPresets.FindActivePresetByEngineIdAsync(OcrEngineIds.MinerU, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<OcrPresetId>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        if (existing.Value is not null)
        {
            return Result<OcrPresetId>.Success(existing.Value.PresetId);
        }

        Result<OcrPreset> created = await _services.OcrPresets.CreatePresetAsync(
            "MinerU OCR",
            "MinerU document OCR preset",
            OcrEngineIds.MinerU,
            OcrModelIds.MinerUDefault,
            null,
            """{"isOcr":true,"enableTable":true,"enableFormula":true}""",
            true,
            cancellationToken);
        return created.IsFailure
            ? Result<OcrPresetId>.Failure(created.ErrorCode!, created.ErrorMessage!)
            : Result<OcrPresetId>.Success(created.Value.PresetId);
    }

    private async Task<Result<OcrPresetId>> EnsureNdlKotenPresetAsync(CancellationToken cancellationToken)
    {
        Result<OcrPreset?> existing =
            await _services.OcrPresets.FindActivePresetByEngineIdAsync(OcrEngineIds.NdlKoten, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<OcrPresetId>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        if (existing.Value is not null)
        {
            return Result<OcrPresetId>.Success(existing.Value.PresetId);
        }

        Result<OcrPreset> created = await _services.OcrPresets.CreatePresetAsync(
            "NDL Koten OCR Lite",
            "Local classical Japanese OCR preset",
            OcrEngineIds.NdlKoten,
            OcrModelIds.NdlKotenDefault,
            _services.OcrStorage.NdlKotenModelsDirectory,
            "{}",
            true,
            cancellationToken);
        return created.IsFailure
            ? Result<OcrPresetId>.Failure(created.ErrorCode!, created.ErrorMessage!)
            : Result<OcrPresetId>.Success(created.Value.PresetId);
    }

    private async Task<Result<OcrPresetId>> EnsureNdlLitePresetAsync(CancellationToken cancellationToken)
    {
        Result<OcrPreset?> existing =
            await _services.OcrPresets.FindActivePresetByEngineIdAsync(OcrEngineIds.NdlLite, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<OcrPresetId>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        if (existing.Value is not null)
        {
            return Result<OcrPresetId>.Success(existing.Value.PresetId);
        }

        Result<OcrPreset> created = await _services.OcrPresets.CreatePresetAsync(
            "NDLOCR-Lite",
            "Local Japanese OCR preset for ndlocr-lite",
            OcrEngineIds.NdlLite,
            OcrModelIds.NdlLiteDefault,
            _services.OcrStorage.NdlLiteModelsDirectory,
            "{}",
            true,
            cancellationToken);
        return created.IsFailure
            ? Result<OcrPresetId>.Failure(created.ErrorCode!, created.ErrorMessage!)
            : Result<OcrPresetId>.Success(created.Value.PresetId);
    }

    private async Task<Result<OcrPresetId>> EnsureRapidOcrPresetAsync(CancellationToken cancellationToken)
    {
        Result<OcrPreset?> existing =
            await _services.OcrPresets.FindActivePresetByEngineIdAsync(OcrEngineIds.RapidOcr, cancellationToken);
        if (existing.IsFailure)
        {
            return Result<OcrPresetId>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        if (existing.Value is not null)
        {
            return Result<OcrPresetId>.Success(existing.Value.PresetId);
        }

        // Empty parameters keep the RapidOCR ONNX pipeline on the upstream v3.9.2 defaults.
        Result<OcrPreset> created = await _services.OcrPresets.CreatePresetAsync(
            "RapidOCR",
            "Local RapidOCR preset using the pinned PP-OCRv6 models",
            OcrEngineIds.RapidOcr,
            OcrModelIds.RapidOcrDefault,
            _services.OcrStorage.RapidOcrModelsDirectory,
            "{}",
            true,
            cancellationToken);
        return created.IsFailure
            ? Result<OcrPresetId>.Failure(created.ErrorCode!, created.ErrorMessage!)
            : Result<OcrPresetId>.Success(created.Value.PresetId);
    }
}
