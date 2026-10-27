using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Llm;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Host;

/// <summary>
///     Shared default presets and queue routing for desktop and agent OCR entry points.
///     Defaults come from <see cref="IOcrEngineCatalog" /> metadata — the declared
///     <see cref="OcrEngineDefinition.DefaultModelId" />, <see cref="OcrEngineDefinition.DefaultConfig" />,
///     and <see cref="OcrEngineDefinition.ModelDirectoryKind" /> — never from a host engine switch.
///     Discovery stays metadata-only: no extension session is created here.
/// </summary>
public sealed class OcrPresetDefaults(
    IOcrPresetService presets,
    OcrStorageLocations storage,
    Func<LlmAppSettings> settings,
    IOcrEngineCatalog? catalog = null,
    Func<string, CancellationToken, Task<Result<OcrPresetVersionWrite>>>? captureComponent = null)
{
    private readonly IOcrPresetService _presets = presets;
    private readonly OcrStorageLocations _storage = storage;

    // Runtime composes the real engine catalog through HostServices; until it is wired, engine
    // resolution must fail explicitly — never invent definitions or fall back to a host switch.
    private readonly IOcrEngineCatalog _catalog = catalog ?? UnavailableCatalog.Instance;

    private sealed class UnavailableCatalog : IOcrEngineCatalog
    {
        public static readonly UnavailableCatalog Instance = new();

        public IReadOnlyList<OcrEngineDefinition> ListDefinitions()
        {
            return [];
        }

        public IReadOnlyList<PdfTextLayerDefinition> ListTextLayerDefinitions()
        {
            return [];
        }

        public string? GetExtensionId(string engineId)
        {
            return null;
        }

        public string? GetTextLayerExtensionId(string providerId)
        {
            return null;
        }

        public Task<Result<OcrEngineDefinition>> GetDefinitionAsync(string engineId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrEngineDefinition>.Failure(AppErrorCodes.NotFound,
                $"OCR engine '{engineId}' is unavailable: the engine catalog is not composed on this host."));
        }
    }

    public async Task<Result<OcrPresetId>> EnsurePresetAsync(string engineId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(engineId))
        {
            return Result<OcrPresetId>.Failure(AppErrorCodes.ValidationFailed,
                "OCR engine ID is required.");
        }

        // The user's existing active preset for the engine wins; only create the managed
        // default preset when the engine has none yet. Implicit ensure is the ONLY caller of
        // the same-engine lookup — explicit selections resolve by id through
        // ResolveSelectionAsync and never reach this path.
        Result<OcrPreset?> existing =
            await _presets.FindActivePresetByEngineIdAsync(engineId.Trim(), cancellationToken);
        if (existing.IsFailure)
        {
            return Result<OcrPresetId>.Failure(existing.ErrorCode!, existing.ErrorMessage!);
        }

        Result<OcrEngineDefinition> definition =
            await _catalog.GetDefinitionAsync(engineId.Trim(), cancellationToken);
        if (definition.IsFailure)
        {
            return Result<OcrPresetId>.Failure(definition.ErrorCode!,
                definition.ErrorMessage ?? $"OCR engine '{engineId}' is not registered on this host.");
        }

        if (existing.Value is { } userPreset && !IsManagedDefault(userPreset, definition.Value))
        {
            return Result<OcrPresetId>.Success(userPreset.PresetId);
        }

        Result<OcrPresetVersionWrite>? captured = captureComponent is not null
            ? await captureComponent(definition.Value.EngineId, cancellationToken).ConfigureAwait(false)
            : null;
        if (captured is { IsFailure: true })
        {
            return Result<OcrPresetId>.Failure(captured.ErrorCode!, captured.ErrorMessage!);
        }

        string modelId = captured?.Value.ModelId ?? ResolveModelId(definition.Value);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return Result<OcrPresetId>.Failure(AppErrorCodes.ValidationFailed,
                $"请先在设置中为 OCR 引擎 {definition.Value.DisplayName} 配置模型。");
        }

        string? modelPath = captured?.Value.ModelPath ??
                            _storage.ModelDirectoryFor(definition.Value.ModelDirectoryKind);
        string parameters = captured?.Value.ParametersJson ?? WriteDefaultParametersJson(definition.Value);
        if (existing.Value is { } preset)
        {
            // Host-managed defaults follow definition/settings drift (e.g. a changed OCR model)
            // through a NEW immutable version; user-owned presets are never rewritten.
            if (!IsManagedDefault(preset, definition.Value))
            {
                return Result<OcrPresetId>.Success(preset.PresetId);
            }

            Result<OcrPresetVersion> version =
                await _presets.GetCurrentVersionAsync(preset.PresetId, cancellationToken);
            if (version.IsFailure)
            {
                return Result<OcrPresetId>.Failure(version.ErrorCode!, version.ErrorMessage!);
            }

            if (version.Value.ModelId != modelId ||
                !string.Equals(version.Value.ModelPath, modelPath, StringComparison.Ordinal) ||
                !string.Equals(version.Value.ParametersJson, parameters, StringComparison.Ordinal))
            {
                Result<OcrPresetVersion> updated = await _presets.CreatePresetVersionAsync(
                    preset.PresetId, definition.Value.EngineId, modelId, modelPath,
                    parameters, version.Value.ApplyOnSuccess,
                    cancellationToken);
                if (updated.IsFailure)
                {
                    return Result<OcrPresetId>.Failure(updated.ErrorCode!, updated.ErrorMessage!);
                }
            }

            return Result<OcrPresetId>.Success(preset.PresetId);
        }

        Result<OcrPreset> created = await _presets.CreatePresetAsync(
            definition.Value.DisplayName,
            ManagedDescription(definition.Value),
            definition.Value.EngineId,
            modelId,
            modelPath,
            parameters,
            true,
            cancellationToken);
        return created.IsFailure
            ? Result<OcrPresetId>.Failure(created.ErrorCode!, created.ErrorMessage!)
            : Result<OcrPresetId>.Success(created.Value.PresetId);
    }

    /// <summary>
    ///     Materializes the managed default preset for every registered engine that has none yet.
    ///     Presets are the only unit the pickers and menus can offer, so a shipped engine whose
    ///     implicit-resolution path never ran would otherwise stay unselectable. Engines are
    ///     ensured independently and in display order: a default that cannot be frozen (no
    ///     configured model, an untrusted working draft) skips only itself instead of failing
    ///     the enumeration.
    /// </summary>
    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
    {
        foreach (OcrEngineDefinition definition in _catalog.ListDefinitions()
                     .OrderBy(value => value.DisplayName, StringComparer.Ordinal)
                     .ThenBy(value => value.EngineId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await EnsurePresetAsync(definition.EngineId, cancellationToken).ConfigureAwait(false);
        }
    }

    public (string AdapterKind, string? ProviderId) QueueRoutingFor(string engineId,
        OcrEngineDefinition? definition = null)
    {
        definition ??= _catalog.ListDefinitions().FirstOrDefault(value =>
            string.Equals(value.EngineId, engineId, StringComparison.OrdinalIgnoreCase));
        return OcrQueueRouting.ForEngine(engineId, definition,
            definition?.AdapterKind == OcrAdapterKind.CloudApi ? settings().OcrSelection.ProviderId : null);
    }

    /// <summary>
    ///     Definition-declared model wins; cloud/LLM-bound engines that declare no default model
    ///     inherit the configured multimodal OCR model so the managed preset tracks settings.
    /// </summary>
    private string ResolveModelId(OcrEngineDefinition definition)
    {
        return !string.IsNullOrWhiteSpace(definition.DefaultModelId)
            ? definition.DefaultModelId
            : settings().OcrSelection.Model;
    }

    private static string ManagedDescription(OcrEngineDefinition definition)
    {
        return string.IsNullOrWhiteSpace(definition.Notes)
            ? $"Default OCR preset for {definition.DisplayName}"
            : definition.Notes;
    }

    private static bool IsManagedDefault(OcrPreset preset, OcrEngineDefinition definition)
    {
        return string.Equals(preset.Name, definition.DisplayName, StringComparison.Ordinal) &&
               string.Equals(preset.Description, ManagedDescription(definition), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Seeds the version envelope from definition defaults. Extension package identity is
    ///     resolved by the runtime catalog at acquisition, never guessed from the engine id.
    /// </summary>
    private static string WriteDefaultParametersJson(OcrEngineDefinition definition)
    {
        return OcrPresetConfiguration.WriteEnvelopeJson(
            null,
            definition.ConfigurationVersion,
            definition.DefaultConfig,
            null,
            1,
            null);
    }

    /// <summary>
    ///     Registry-sourced engine metadata for settings screens and editors — schema/default
    ///     discovery only; this never creates extension OCR sessions.
    /// </summary>
    public IReadOnlyList<OcrEngineDefinition> ListEngineDefinitions()
    {
        return _catalog.ListDefinitions();
    }

    /// <summary>One engine definition by id; NotFound for unregistered engines.</summary>
    public Task<Result<OcrEngineDefinition>> GetEngineDefinitionAsync(string engineId,
        CancellationToken cancellationToken = default)
    {
        return _catalog.GetDefinitionAsync(engineId, cancellationToken);
    }

    /// <summary>
    ///     Declarative text-layer provider metadata for pickers/editors — schema fields and
    ///     defaults arrive on <see cref="PdfTextLayerDefinition.DefaultConfig" />; listing never
    ///     activates a package or opens a session.
    /// </summary>
    public IReadOnlyList<PdfTextLayerDefinition> ListTextLayerDefinitions()
    {
        return _catalog.ListTextLayerDefinitions();
    }

    /// <summary>
    ///     The extension package id that implements one engine — metadata-only lookup used by the
    ///     preset editor to read the attributed schema; null when no package maps the engine.
    /// </summary>
    public string? GetEngineExtensionId(string engineId)
    {
        return _catalog.GetExtensionId(engineId);
    }

    /// <summary>
    ///     The extension package id that implements one text-layer provider — same metadata-only
    ///     contract as <see cref="GetEngineExtensionId" />.
    /// </summary>
    public string? GetTextLayerExtensionId(string providerId)
    {
        return _catalog.GetTextLayerExtensionId(providerId);
    }
}
