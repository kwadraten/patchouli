using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.FSharp.Core;
using Patchouli.Llm;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Host.Workflows;

/// <summary>One field error shared by editor, menu launch and MCP validation.</summary>
public sealed record WorkflowValidationIssue(
    string? Key,
    string Code,
    string Message,
    int? Line = null,
    int? Column = null,
    string? ScopeTarget = null);

/// <summary>Thrown when a workflow launch cannot be resolved into a valid frozen input.</summary>
public sealed class WorkflowConfigurationValidationException : Exception
{
    public WorkflowConfigurationValidationException(IReadOnlyList<WorkflowValidationIssue> issues)
        : base(string.Join(Environment.NewLine, issues.Select(issue => issue.Message)))
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues;
    }

    public IReadOnlyList<WorkflowValidationIssue> Issues { get; }
}

/// <summary>A declaration and its values as currently stored for one workflow.</summary>
public sealed record WorkflowConfigurationSnapshot(
    WorkflowDeclarationAnalysis Analysis,
    IReadOnlyDictionary<string, string> Values,
    string DeclarationFingerprint,
    IReadOnlyList<WorkflowValidationIssue> Issues);

/// <summary>The result of saving values without changing the workflow script.</summary>
public sealed record WorkflowConfigurationSaveResult(
    bool Saved,
    string DeclarationFingerprint,
    IReadOnlyList<WorkflowValidationIssue> Issues);

/// <summary>The fully merged, validated launch configuration.</summary>
public sealed record WorkflowConfigurationResolution(
    WorkflowDeclarationAnalysis Analysis,
    IReadOnlyDictionary<string, string> Values,
    string DeclarationFingerprint,
    ModelSelection? ModelSelection,
    IReadOnlyList<WorkflowValidationIssue> Issues)
{
    public bool Succeeded => Issues.Count == 0;
}

/// <summary>
///     Discovers fsx parameter declarations, stores per-workflow values, migrates the old translation
///     settings once, and resolves the same validated launch values for UI and MCP callers.
/// </summary>
public sealed class WorkflowConfigurationService
{
    private const int StoreSchemaVersion = 1;

    private static readonly IReadOnlyDictionary<string, string> EmptyValues =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly WorkflowStore _store;
    private readonly Func<LlmAppSettings> _settings;
    private readonly ScriptCompiler _compiler = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkflowConfigurationService(WorkflowStore store, Func<LlmAppSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _settings = settings;
    }

    /// <summary>Reads the current declaration and its saved stable-key values.</summary>
    public async Task<WorkflowConfigurationSnapshot> ReadAsync(string workflowId, string? scriptText = null,
        string? entryPoint = null, CancellationToken cancellationToken = default)
    {
        FSharpOption<WorkflowDefinition>? definition =
            await _store.TryLoadAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            throw new KeyNotFoundException($"The workflow '{workflowId}' does not exist.");
        }

        if (scriptText is null)
        {
            FSharpOption<string>? loadedScript = await _store.ReadScriptAsync(workflowId, cancellationToken)
                .ConfigureAwait(false);
            scriptText = loadedScript?.Value ?? string.Empty;
        }

        string effectiveEntryPoint = string.IsNullOrWhiteSpace(entryPoint)
            ? definition.Value.ScriptEntryPoint
            : entryPoint;
        WorkflowDeclarationAnalysis analysis = await AnalyzeAsync(workflowId, scriptText, effectiveEntryPoint)
            .ConfigureAwait(false);
        string fingerprint = Fingerprint(analysis);
        IReadOnlyDictionary<string, string> values = await ReadValuesAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        return new WorkflowConfigurationSnapshot(analysis, values, fingerprint, DeclarationIssues(analysis));
    }

    /// <summary>Persists the generated metadata projection after the corresponding script is saved.</summary>
    public async Task SaveDeclarationProjectionAsync(string workflowId, WorkflowDeclarationAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (!analysis.Succeeded)
        {
            throw new WorkflowConfigurationValidationException(DeclarationIssues(analysis));
        }

        FSharpOption<WorkflowDefinition>? loaded =
            await _store.TryLoadAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            throw new KeyNotFoundException($"The workflow '{workflowId}' does not exist.");
        }

        if (loaded.Value.BuiltIn)
        {
            return;
        }

        if (!MetadataMatches(loaded.Value, analysis))
        {
            await BackupLegacyDefinitionIfNeededAsync(workflowId, cancellationToken).ConfigureAwait(false);
            WorkflowMutationResult result = await _store.SaveAsync(ProjectDefinition(loaded.Value, analysis),
                cancellationToken).ConfigureAwait(false);
            if (!result.IsApplied)
            {
                throw new InvalidOperationException(
                    $"The generated workflow metadata could not be saved for '{workflowId}'.");
            }
        }
    }

    /// <summary>Lists definitions projected from SDK script declarations for UI and MCP surfaces.</summary>
    public async Task<IReadOnlyList<WorkflowDefinition>> DiscoverDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WorkflowDefinition> stored =
            await _store.ListDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        List<WorkflowDefinition> discovered = new(stored.Count);
        foreach (WorkflowDefinition definition in stored)
        {
            WorkflowConfigurationSnapshot snapshot = await ReadAsync(definition.Id,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            discovered.Add(snapshot.Analysis.Succeeded ? ProjectDefinition(definition, snapshot.Analysis) : definition);
        }

        return discovered;
    }

    /// <summary>Saves values against the declaration fingerprint the editor displayed.</summary>
    public async Task<WorkflowConfigurationSaveResult> SaveAsync(string workflowId, string declarationFingerprint,
        IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        WorkflowConfigurationSnapshot snapshot = await ReadAsync(workflowId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(snapshot.DeclarationFingerprint, declarationFingerprint, StringComparison.Ordinal))
        {
            WorkflowValidationIssue conflict = new(null, "declaration_changed",
                "工作流声明已更改。请刷新表单后再保存。");
            return new WorkflowConfigurationSaveResult(false, snapshot.DeclarationFingerprint, [conflict]);
        }

        HashSet<string> declaredKeys = snapshot.Analysis.Fields
            .Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        HashSet<string> contextKeys = snapshot.Analysis.Fields
            .Where(field => !string.IsNullOrWhiteSpace(field.ContextBinding))
            .Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> normalized = NormalizeValues(values)
            .Where(pair => declaredKeys.Contains(pair.Key) && !contextKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        WorkflowParameterIssue[] validation = WorkflowParameterResolver.Validate(snapshot.Analysis, normalized);
        WorkflowValidationIssue[] issues = validation
            .Where(issue => !contextKeys.Contains(issue.Key))
            .Select(ToIssue).ToArray();
        if (snapshot.Issues.Count > 0)
        {
            issues = [.. snapshot.Issues, .. issues];
        }

        if (issues.Length > 0)
        {
            return new WorkflowConfigurationSaveResult(false, snapshot.DeclarationFingerprint, issues);
        }

        await WriteValuesAsync(workflowId, snapshot.DeclarationFingerprint, normalized, cancellationToken)
            .ConfigureAwait(false);
        return new WorkflowConfigurationSaveResult(true, snapshot.DeclarationFingerprint, []);
    }

    /// <summary>Resolves explicit, context, saved and declared-default values in the SDK's fixed precedence.</summary>
    public async Task<WorkflowConfigurationResolution> ValidateLaunchAsync(string workflowId,
        IReadOnlyDictionary<string, string>? explicitValues,
        IReadOnlyDictionary<string, string>? contextValues,
        CancellationToken cancellationToken = default)
    {
        WorkflowConfigurationSnapshot snapshot = await ReadAsync(workflowId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return ResolveLaunch(snapshot, explicitValues, contextValues);
    }

    /// <summary>Resolves an already analyzed script snapshot, preventing a reread race at launch.</summary>
    public WorkflowConfigurationResolution ResolveLaunch(WorkflowConfigurationSnapshot snapshot,
        IReadOnlyDictionary<string, string>? explicitValues,
        IReadOnlyDictionary<string, string>? contextValues)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyDictionary<string, string> saved = snapshot.Values;
        ParameterResolutionResult resolution = WorkflowParameterResolver.ResolveParameters(snapshot.Analysis,
            explicitValues ?? EmptyValues, saved, contextValues ?? EmptyValues);
        List<WorkflowValidationIssue> issues = [.. snapshot.Issues];
        if (!resolution.Succeeded)
        {
            issues.AddRange(resolution.Issues.Select(ToIssue));
        }

        ModelSelection? model = null;
        string? modelKey = snapshot.Analysis.SessionModelKey;
        if (string.IsNullOrWhiteSpace(modelKey))
        {
            issues.Add(new WorkflowValidationIssue(null, "model_binding_missing",
                "该工作流未声明会话模型。请在 fsx 中通过 Workflow.withModel 绑定模型参数。"));
        }
        else if (resolution.Values.TryGetValue(modelKey, out string? encoded))
        {
            try
            {
                model = ModelSelectionCodec.Decode(encoded);
            }
            catch (ArgumentException exception)
            {
                issues.Add(new WorkflowValidationIssue(modelKey, "invalid_model_selection", exception.Message));
            }
            catch (JsonException exception)
            {
                issues.Add(new WorkflowValidationIssue(modelKey, "invalid_model_selection", exception.Message));
            }
        }

        return new WorkflowConfigurationResolution(snapshot.Analysis, resolution.Values,
            snapshot.DeclarationFingerprint, model, issues);
    }

    /// <summary>Copies saved values to a new workflow id, retaining stable keys.</summary>
    public async Task CopyAsync(string sourceWorkflowId, string targetWorkflowId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, string> values = await ReadValuesAsync(sourceWorkflowId, cancellationToken)
            .ConfigureAwait(false);
        string fingerprint = await ReadFingerprintAsync(sourceWorkflowId, cancellationToken).ConfigureAwait(false);
        await WriteValuesAsync(targetWorkflowId, fingerprint, values, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Performs the one-time old translation-settings migration after built-in declarations are available.</summary>
    public async Task MigrateLegacyAsync(CancellationToken cancellationToken = default)
    {
        await new WorkflowLegacyDeclarationMigrator(_store, _compiler).MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        FSharpOption<WorkflowDefinition>? definition =
            await _store.TryLoadAsync(WorkflowIds.FullTextTranslation, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return;
        }

        WorkflowConfigurationSnapshot snapshot = await ReadAsync(WorkflowIds.FullTextTranslation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _ = await MigrateLegacyTranslationValuesAsync(snapshot.Analysis, snapshot.DeclarationFingerprint,
            snapshot.Values, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Generates the legacy workflow-definition parameter projection from current SDK declarations.</summary>
    public static WorkflowParameter[] ProjectLegacyParameters(WorkflowDeclarationAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.Fields.Select(field => new WorkflowParameter(
            field.Key,
            field.Type switch
            {
                WorkflowParameterValueType.Integer => WorkflowParameterType.Integer,
                WorkflowParameterValueType.Decimal => WorkflowParameterType.Decimal,
                WorkflowParameterValueType.Boolean => WorkflowParameterType.Boolean,
                WorkflowParameterValueType.Choice => WorkflowParameterType.Choice,
                WorkflowParameterValueType.Language => WorkflowParameterType.Language,
                WorkflowParameterValueType.Model => WorkflowParameterType.Model,
                WorkflowParameterValueType.Documents => WorkflowParameterType.Documents,
                WorkflowParameterValueType.PageRange => WorkflowParameterType.PageRange,
                WorkflowParameterValueType.TextSelection => WorkflowParameterType.TextSelection,
                WorkflowParameterValueType.MultilineText => WorkflowParameterType.MultilineText,
                _ => WorkflowParameterType.Text
            },
            field.Required,
            field.Description,
            field.DefaultValue)).ToArray();
    }

    /// <summary>Returns the schema-safe path for backing up old external parameter metadata.</summary>
    public async Task BackupLegacyDefinitionIfNeededAsync(string workflowId,
        CancellationToken cancellationToken = default)
    {
        string backupPath = _store.ResolveLegacyBackupPath(workflowId);
        if (File.Exists(backupPath))
        {
            return;
        }

        string definitionPath = _store.ResolveDefinitionPath(workflowId);
        if (!File.Exists(definitionPath))
        {
            return;
        }

        string json = await File.ReadAllTextAsync(definitionPath, cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(backupPath, json, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowDeclarationAnalysis> AnalyzeAsync(string workflowId, string script, string entryPoint)
    {
        string fileName = _store.ResolveScriptPath(workflowId);
        return await _compiler.AnalyzeWorkflowAsync(script, fileName, entryPoint).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, string>> MigrateLegacyTranslationValuesAsync(
        WorkflowDeclarationAnalysis analysis, string fingerprint, IReadOnlyDictionary<string, string> existing,
        CancellationToken cancellationToken)
    {
        ConfigurationFile? stored = await ReadConfigurationFileAsync(WorkflowIds.FullTextTranslation,
            cancellationToken).ConfigureAwait(false);
        if (stored?.LegacySettingsMigrated == true)
        {
            return stored.Values;
        }

        IReadOnlyDictionary<string, string> legacyValues = _settings().LegacyWorkflowValues;
        if (stored is not null)
        {
            await WriteConfigurationFileAsync(WorkflowIds.FullTextTranslation,
                    stored with { LegacySettingsMigrated = true, LegacyValuesBackup = legacyValues }, cancellationToken)
                .ConfigureAwait(false);
            return stored.Values;
        }

        Dictionary<string, string> migrated = existing.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        foreach (string key in legacyValues.Keys)
        {
            if (analysis.Fields.Any(field => string.Equals(field.Key, key, StringComparison.Ordinal)) &&
                legacyValues.TryGetValue(key, out string? value))
            {
                migrated.TryAdd(key, value);
            }
        }

        await WriteConfigurationFileAsync(WorkflowIds.FullTextTranslation,
                new ConfigurationFile(StoreSchemaVersion, fingerprint, migrated, true, legacyValues), cancellationToken)
            .ConfigureAwait(false);

        return migrated;
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadValuesAsync(string workflowId,
        CancellationToken cancellationToken)
    {
        ConfigurationFile? stored = await ReadConfigurationFileAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        return stored?.Values ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task<string> ReadFingerprintAsync(string workflowId, CancellationToken cancellationToken)
    {
        ConfigurationFile? stored = await ReadConfigurationFileAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        return stored?.Fingerprint ?? string.Empty;
    }

    private async Task<ConfigurationFile?> ReadConfigurationFileAsync(string workflowId,
        CancellationToken cancellationToken)
    {
        string path = _store.ResolveConfigurationPath(workflowId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(path);
        ConfigurationFile? stored = await JsonSerializer.DeserializeAsync<ConfigurationFile>(stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.SchemaVersion != StoreSchemaVersion || stored.Values is null)
        {
            throw new InvalidDataException($"The saved configuration for '{workflowId}' is invalid.");
        }

        return stored;
    }

    private Task WriteValuesAsync(string workflowId, string fingerprint, IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        return WriteConfigurationFileAsync(workflowId,
            new ConfigurationFile(StoreSchemaVersion, fingerprint, NormalizeValues(values), false,
                new Dictionary<string, string>(StringComparer.Ordinal)), cancellationToken);
    }

    private async Task WriteConfigurationFileAsync(string workflowId, ConfigurationFile configuration,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = _store.ResolveConfigurationPath(workflowId);
            string json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true });
            await WriteAtomicAsync(path, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> NormalizeValues(
        IReadOnlyDictionary<string, string> values)
    {
        Dictionary<string, string> normalized = new(StringComparer.Ordinal);
        foreach ((string key, string value) in values)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                normalized[key] = value ?? string.Empty;
            }
        }

        return normalized;
    }

    private static bool MetadataMatches(WorkflowDefinition definition, WorkflowDeclarationAnalysis analysis)
    {
        return string.Equals(definition.Name, analysis.Info.Name, StringComparison.Ordinal) &&
               string.Equals(definition.Description, analysis.Info.Description, StringComparison.Ordinal) &&
               string.Equals(definition.ScriptEntryPoint, analysis.Info.EntryPoint, StringComparison.Ordinal) &&
               definition.SelectionScope == analysis.Info.SelectionScope &&
               string.Equals(definition.Menu.MenuPath, analysis.Info.MenuPath, StringComparison.Ordinal) &&
               definition.Menu.Order == analysis.Info.MenuOrder &&
               definition.Menu.ShowInMenu == analysis.Info.ShowInMenu &&
               definition.Parameters.SequenceEqual(ProjectLegacyParameters(analysis));
    }

    private static string Fingerprint(WorkflowDeclarationAnalysis analysis)
    {
        var canonical = analysis.Fields.Select(field => new
        {
            field.Key,
            field.Label,
            Type = field.Type.ToString(),
            field.Required,
            field.Description,
            field.HasDefault,
            field.DefaultValue,
            Minimum = field.Minimum.HasValue ? field.Minimum.Value.ToString(CultureInfo.InvariantCulture) : null,
            Maximum = field.Maximum.HasValue ? field.Maximum.Value.ToString(CultureInfo.InvariantCulture) : null,
            Choices = field.Choices,
            field.ContextBinding
        }).ToArray();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            analysis.Info.Name,
            analysis.Info.Description,
            analysis.Info.EntryPoint,
            Scope = (int)analysis.Info.SelectionScope,
            analysis.Info.MenuPath,
            analysis.Info.MenuOrder,
            analysis.Info.ShowInMenu,
            analysis.SessionModelKey,
            Fields = canonical
        });
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static IReadOnlyList<WorkflowValidationIssue> DeclarationIssues(WorkflowDeclarationAnalysis analysis)
    {
        if (analysis.Succeeded)
        {
            return [];
        }

        return analysis.Diagnostics
            .Where(diagnostic => diagnostic.Severity == ScriptDiagnosticSeverity.Error)
            .Select(diagnostic => new WorkflowValidationIssue(null, diagnostic.ErrorNumber,
                diagnostic.Message, diagnostic.Line, diagnostic.Column))
            .ToArray();
    }

    private static WorkflowValidationIssue ToIssue(WorkflowParameterIssue issue)
    {
        return new WorkflowValidationIssue(issue.Key, issue.Code, issue.Message, issue.Line, issue.Column);
    }

    /// <summary>Projects workflow metadata and compatibility parameters from a successful SDK analysis.</summary>
    public static WorkflowDefinition ProjectDefinition(WorkflowDefinition existing,
        WorkflowDeclarationAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(analysis);
        WorkflowDeclarationInfo info = analysis.Info;
        return new WorkflowDefinition(existing.Id, info.Name, info.Description, info.EntryPoint,
            ProjectLegacyParameters(analysis), info.SelectionScope,
            existing.Locked, new WorkflowMenuPlacement(info.MenuPath, info.MenuOrder, info.ShowInMenu),
            existing.BuiltIn);
    }

    private sealed record ConfigurationFile(
        int SchemaVersion,
        string Fingerprint,
        IReadOnlyDictionary<string, string> Values,
        bool LegacySettingsMigrated,
        IReadOnlyDictionary<string, string> LegacyValuesBackup);
}
