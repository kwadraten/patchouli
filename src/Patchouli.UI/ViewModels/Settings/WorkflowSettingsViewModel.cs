using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.FSharp.Core;
using Patchouli.Host.Composition;
using Patchouli.Host.Workflows;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
///     「工作流」settings section: the workflow list and the editor of one workflow definition
///     (ADR 0036, plan §3.7). The list shows the built-ins first — they are registered in code and
///     locked, so they can neither be edited nor deleted; the list reports that through a lock badge
///     and disabled edit/delete actions. Creation starts with a runnable reply-only example;
///     copying duplicates the selected saved definition and script into an unlocked workflow.
/// </summary>
/// <remarks>
///     <para>
///         Workflows are saved individually and immediately (definition, then script), so this section
///         does not participate in the settings page's debounced draft pipeline:
///         <see cref="SupportsEditing" /> stays false and nothing here is ever flushed implicitly.
///     </para>
///     <para>
///         The script text is compiled by <see cref="ScriptCompiler" /> before anything is written. A
///         script that does not compile is reported with line, column and message, and the save is
///         blocked: the editor never writes a .fsx that the executor would reject at launch.
///     </para>
/// </remarks>
public sealed partial class WorkflowSettingsViewModel : SettingsSectionViewModelBase
{
    private static readonly Regex WorkflowInfoNameLiteral = new(
        @"(?m)^[ \t]*let[ \t]+info(?:[ \t]*:[ \t]*WorkflowInfo)?[ \t]*=[ \t]*(?:\r?\n[ \t]*)?(?:Patchouli\.Workflows\.Scripting\.)?WorkflowInfo\.create[ \t\r\n]+(?<name>""(?:\\.|[^""\\])*"")",
        RegexOptions.CultureInvariant);

    /// <summary>The trust-model warning shown in every editor area (ADR 0036, plan §3.2).</summary>
    public const string TrustWarning =
        ".fsx 脚本以宿主权限在进程内执行，不是沙箱；只保存你信任的本地脚本。";

    private readonly MainWindowViewModel _main;
    private readonly string _libraryPath;
    private WorkflowStore? _store;
    private WorkflowConfigurationService? _configurationService;
    private bool _isRefreshing;
    private Task _pendingEditorLoad = Task.CompletedTask;

    public WorkflowSettingsViewModel(MainWindowViewModel main)
        : this(main, main.RuntimeDatabasePath)
    {
    }

    public WorkflowSettingsViewModel(MainWindowViewModel main, string libraryPath)
        : this(main, libraryPath, null)
    {
    }

    internal WorkflowSettingsViewModel(MainWindowViewModel main, string libraryPath, WorkflowStore? store)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);

        _main = main;
        _libraryPath = libraryPath;
        _store = store;
        _configurationService = store is null
            ? null
            : new WorkflowConfigurationService(store, () => _main.AppOptions.Llm);
        Editor = new WorkflowEditorViewModel(this, store);
        CopyCommand = new AsyncCommand(async () =>
        {
            if (await CopyAsync())
            {
                await OpenEditorAsync(SelectedWorkflow);
            }
        }, () => CanCopySelected);
        CreateNewCommand = new AsyncCommand(async () =>
        {
            if (await CreateNewAsync())
            {
                await OpenEditorAsync(SelectedWorkflow);
            }
        });
        DeleteCommand = new AsyncCommand(DeleteAsync, () => CanDeleteSelected);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        SelectWorkflowCommand = new RelayCommand<WorkflowDefinitionItemViewModel>(Select);
        CheckScriptCommand = new AsyncCommand(() => Editor.CheckScriptAsync());
        SaveScriptCommand = new AsyncCommand(() => Editor.SaveAsync(), () => Editor.CanSave);
        Editor.PropertyChanged += OnEditorPropertyChanged;
    }

    /// <summary>Every workflow the Library can launch: the built-ins first, then the stored ones.</summary>
    [ExcludeFromDerivedGeneration]
    public ObservableCollection<WorkflowDefinitionItemViewModel> Workflows { get; } = [];

    /// <summary>The definition the editor is bound to. Locked and built-in entries are read-only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    [NotifyPropertyChangedFor(nameof(DeleteExplanation))]
    [NotifyPropertyChangedFor(nameof(SelectedSummary))]
    public partial WorkflowDefinitionItemViewModel? SelectedWorkflow { get; set; }

    public WorkflowEditorViewModel Editor { get; }

    public AsyncCommand CopyCommand { get; }
    public AsyncCommand CreateNewCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public RelayCommand<WorkflowDefinitionItemViewModel> SelectWorkflowCommand { get; }
    public AsyncCommand CheckScriptCommand { get; }
    public AsyncCommand SaveScriptCommand { get; }

    [ExcludeFromDerivedGeneration] public bool CanCopySelected => SelectedWorkflow is not null && !Editor.IsSaving;

    /// <summary>True while a definition is selected and is neither built-in, locked nor busy.</summary>
    [ExcludeFromDerivedGeneration]
    public bool CanDeleteSelected =>
        SelectedWorkflow is { IsLocked: false, IsBuiltIn: false } && !Editor.IsSaving;

    /// <summary>Why the delete action is unavailable, empty when deletion is available.</summary>
    [ExcludeFromDerivedGeneration]
    public string DeleteExplanation => SelectedWorkflow switch
    {
        null => "先在列表中选择一个工作流。",
        { IsBuiltIn: true } => "内置工作流随 Patchouli 发布并始终锁定：不允许修改或删除。",
        { IsLocked: true } => "该工作流已锁定，不允许修改或删除。",
        _ => ""
    };

    [ExcludeFromDerivedGeneration]
    public string SelectedSummary => SelectedWorkflow is { } item
        ? $"{item.Id} · {item.MenuPlacementText} · {item.ScopeText}"
        : "";

    public override bool SupportsEditing => false;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => false;

    [ExcludeFromDerivedGeneration] public override bool CanSave => false;

    /// <summary>The real F# compile check shared with the executor.</summary>
    internal ScriptCompiler Compiler { get; } = new();

    /// <summary>Reads the workflow directory of the Library the settings page is bound to.</summary>
    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync();
    }

    public override Task SaveAsync()
    {
        // Every workflow edit is committed by its own explicit save action; there is no draft to flush.
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        Editor.ResetToSelected();
        Status = "已放弃未保存的工作流编辑";
        return Task.CompletedTask;
    }

    public async Task RefreshAsync()
    {
        WorkflowStore store;
        try
        {
            store = await ResolveStoreAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = $"无法打开工作流目录：{exception.Message}";
            Status = "读取工作流失败";
            SaveState = SettingsSaveState.Failed;
            return;
        }

        IReadOnlyList<WorkflowDefinition> definitions;
        try
        {
            definitions = await Configuration.DiscoverDefinitionsAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = $"无法读取工作流列表：{exception.Message}";
            Status = "读取工作流失败";
            SaveState = SettingsSaveState.Failed;
            return;
        }

        string? selectedId = SelectedWorkflow?.Id;
        Workflows.Clear();
        foreach (WorkflowDefinition definition in definitions)
        {
            Workflows.Add(new WorkflowDefinitionItemViewModel(this, store, definition));
        }

        LastError = null;
        SaveState = SettingsSaveState.Saved;
        Status = Workflows.Count == 0
            ? "没有工作流"
            : $"共 {Workflows.Count} 个工作流（内置 {Workflows.Count(item => item.IsBuiltIn)} 个）";
        _isRefreshing = true;
        try
        {
            SelectedWorkflow = Workflows.FirstOrDefault(item => item.Id == selectedId) ?? Workflows.FirstOrDefault();
        }
        finally
        {
            _isRefreshing = false;
        }

        await Editor.LoadAsync(SelectedWorkflow);
    }

    /// <summary>
    ///     The editor load started by the last selection change. Selecting a row must stay synchronous,
    ///     so the script read runs in the background; every read of the editor state (save, check, a
    ///     test) awaits it first, which keeps a stale read from overwriting a fresh draft.
    /// </summary>
    public Task WaitForEditorLoadAsync()
    {
        return _pendingEditorLoad;
    }

    partial void OnSelectedWorkflowChanged(WorkflowDefinitionItemViewModel? value)
    {
        foreach (WorkflowDefinitionItemViewModel item in Workflows)
        {
            item.IsSelected = ReferenceEquals(item, value);
        }

        Raise(nameof(DeleteExplanation));
        Raise(nameof(SelectedSummary));
        Raise(nameof(CanCopySelected));
        CopyCommand.NotifyCanExecuteChanged();
        (DeleteCommand as IRelayCommand).NotifyCanExecuteChanged();
        if (!_isRefreshing)
        {
            _pendingEditorLoad = Editor.LoadAsync(value);
        }
    }

    /// <summary>Selects one row; the editor loads that definition.</summary>
    internal void Select(WorkflowDefinitionItemViewModel? item)
    {
        if (item is not null)
        {
            SelectedWorkflow = item;
        }
    }

    public async Task OpenEditorAsync(WorkflowDefinitionItemViewModel? item,
        IReadOnlyList<WorkflowValidationIssue>? issues = null, WorkflowLaunchSelection? selection = null)
    {
        if (item is null)
        {
            return;
        }

        Select(item);
        await WaitForEditorLoadAsync();
        Editor.SetLaunchContext(selection);
        using Dialogs.WorkflowEditorDialogViewModel dialog = new(this);
        if (issues is { Count: > 0 })
        {
            dialog.ApplyValidationIssues(issues);
        }

        await _main.Dialogs.ShowDialogAsync<bool>(dialog);
    }

    /// <summary>
    ///     Creates an unlocked copy of the selected workflow: its saved definition, its declared
    ///     parameters and its script text, under a fresh id.
    /// </summary>
    internal async Task<bool> CopyAsync()
    {
        WorkflowStore store = await ResolveStoreAsync();
        WorkflowDefinition? source = SelectedWorkflow?.Source;
        if (source is null)
        {
            LastError = "先在列表中选择一个工作流。";
            Status = "复制失败";
            SaveState = SettingsSaveState.Failed;
            return false;
        }

        FSharpOption<string>? script = await store.ReadScriptAsync(source.Id, CancellationToken.None);
        string scriptText = script is null ? "" : script.Value;
        WorkflowConfigurationSnapshot sourceDeclaration =
            await Configuration.ReadAsync(source.Id, scriptText, null, CancellationToken.None);
        string sourceName = sourceDeclaration.Analysis.Succeeded
            ? sourceDeclaration.Analysis.Info.Name
            : source.Name;
        string copyName = sourceName + " 副本";
        bool scriptRenamed = TryRewriteWorkflowInfoName(scriptText, copyName, out string copyScriptText);
        if (scriptRenamed)
        {
            WorkflowConfigurationSnapshot candidateDeclaration =
                await Configuration.ReadAsync(source.Id, copyScriptText, null, CancellationToken.None);
            scriptRenamed = HasOnlyWorkflowNameChanged(
                sourceDeclaration.Analysis, candidateDeclaration.Analysis, copyName);
        }

        if (!scriptRenamed)
        {
            // Keep the script declaration authoritative when it uses a form that cannot be patched safely.
            copyName = sourceName;
            copyScriptText = scriptText;
        }

        string id = await NextAvailableIdAsync(store, "user.workflow-copy");
        WorkflowDefinition copy = new(
            id,
            copyName,
            source.Description,
            source.ScriptEntryPoint,
            source.Parameters,
            source.SelectionScope,
            false,
            source.Menu,
            false);

        WorkflowMutationResult saved = await store.SaveAsync(copy, CancellationToken.None);
        if (WorkflowMutations.KindOf(saved) != WorkflowMutationKind.Applied)
        {
            LastError = WorkflowMutations.Describe(saved);
            Status = "复制工作流失败";
            SaveState = SettingsSaveState.Failed;
            return false;
        }

        WorkflowMutationResult scriptSaved = await store.SaveScriptAsync(id, copyScriptText, CancellationToken.None);
        if (WorkflowMutations.KindOf(scriptSaved) != WorkflowMutationKind.Applied)
        {
            LastError = WorkflowMutations.Describe(scriptSaved);
            Status = "复制脚本失败";
            SaveState = SettingsSaveState.Failed;
            return false;
        }

        WorkflowConfigurationSnapshot copiedDeclaration =
            await Configuration.ReadAsync(id, copyScriptText, null, CancellationToken.None);
        if (copiedDeclaration.Analysis.Succeeded)
        {
            await Configuration.SaveDeclarationProjectionAsync(id, copiedDeclaration.Analysis, CancellationToken.None);
        }

        await Configuration.CopyAsync(source.Id, id, CancellationToken.None);

        await RefreshAsync();
        SelectedWorkflow = Workflows.FirstOrDefault(item => item.Id == id);
        await WaitForEditorLoadAsync();
        ValidationState = SettingsValidationState.Valid;
        Status = scriptRenamed
            ? $"已复制出「{copy.Name}」，脚本可编辑。"
            : $"已复制「{copy.Name}」；脚本元数据形式不支持安全改名，因此保留声明名称。";
        return true;
    }

    private static bool TryRewriteWorkflowInfoName(string scriptText, string name, out string rewrittenScript)
    {
        Match match = WorkflowInfoNameLiteral.Match(scriptText);
        if (!match.Success)
        {
            rewrittenScript = scriptText;
            return false;
        }

        Group literal = match.Groups["name"];
        string encodedName = JsonSerializer.Serialize(name);
        rewrittenScript = scriptText[..literal.Index] + encodedName +
                          scriptText[(literal.Index + literal.Length)..];
        return true;
    }

    private static bool HasOnlyWorkflowNameChanged(WorkflowDeclarationAnalysis source,
        WorkflowDeclarationAnalysis candidate, string expectedName)
    {
        if (!source.Succeeded || !candidate.Succeeded ||
            !string.Equals(candidate.Info.Name, expectedName, StringComparison.Ordinal) ||
            !string.Equals(source.Info.Description, candidate.Info.Description, StringComparison.Ordinal) ||
            !string.Equals(source.Info.EntryPoint, candidate.Info.EntryPoint, StringComparison.Ordinal) ||
            source.Info.SelectionScope != candidate.Info.SelectionScope ||
            !string.Equals(source.Info.MenuPath, candidate.Info.MenuPath, StringComparison.Ordinal) ||
            source.Info.MenuOrder != candidate.Info.MenuOrder ||
            source.Info.ShowInMenu != candidate.Info.ShowInMenu ||
            !string.Equals(source.SessionModelKey, candidate.SessionModelKey, StringComparison.Ordinal) ||
            source.Fields.Length != candidate.Fields.Length)
        {
            return false;
        }

        for (int index = 0; index < source.Fields.Length; index++)
        {
            ParameterDescriptor left = source.Fields[index];
            ParameterDescriptor right = candidate.Fields[index];
            if (!string.Equals(left.Key, right.Key, StringComparison.Ordinal) ||
                !string.Equals(left.Label, right.Label, StringComparison.Ordinal) ||
                left.Type != right.Type || left.Required != right.Required ||
                !string.Equals(left.Description, right.Description, StringComparison.Ordinal) ||
                left.HasDefault != right.HasDefault ||
                !string.Equals(left.DefaultValue, right.DefaultValue, StringComparison.Ordinal) ||
                left.Minimum != right.Minimum || left.Maximum != right.Maximum ||
                !(left.Choices ?? []).SequenceEqual(right.Choices ?? [], StringComparer.Ordinal) ||
                !string.Equals(left.ContextBinding, right.ContextBinding, StringComparison.Ordinal) ||
                left.SourceLine != right.SourceLine || left.SourceColumn != right.SourceColumn)
            {
                return false;
            }
        }

        return true;
    }

    private const string NewWorkflowScript = """
                                             open Patchouli.Workflows.Scripting
                                             open Patchouli.Workflows

                                             let info : WorkflowInfo =
                                                 WorkflowInfo.create "新工作流" "自定义 agent 工作流。"
                                                 |> WorkflowInfo.selectionScope WorkflowSelectionScope.DocumentsAndPages
                                                 |> WorkflowInfo.menu "Tools/Workflows" 100

                                             let model = Parameter.model "model" "执行模型"

                                             let reply =
                                                 Agent.text "reply" "只作简单回复。"
                                                     (fun (_: WorkflowInput) -> "你好！这是一个最小工作流示例。请简单回复一句问候。")
                                                 |> Agent.withTools []
                                                 |> Agent.withBudget (AgentBudget.create 1 0)

                                             let run : AgentWorkflow =
                                                 workflow { step reply }
                                                 |> Workflow.define
                                                 |> Workflow.withModel model
                                             """;

    /// <summary>Creates a runnable, reply-only user workflow with the default menu placement.</summary>
    internal async Task<bool> CreateNewAsync()
    {
        WorkflowStore store = await ResolveStoreAsync();
        string id = await NextAvailableIdAsync(store, "user.workflow");
        WorkflowDefinition created = WorkflowDefinitions.create(id, "新工作流", "", "run");
        WorkflowMutationResult result = await store.SaveAsync(created, CancellationToken.None);
        if (WorkflowMutations.KindOf(result) != WorkflowMutationKind.Applied)
        {
            LastError = WorkflowMutations.Describe(result);
            Status = "新建工作流失败";
            SaveState = SettingsSaveState.Failed;
            return false;
        }

        WorkflowMutationResult scriptSaved = await store.SaveScriptAsync(id, NewWorkflowScript, CancellationToken.None);
        if (WorkflowMutations.KindOf(scriptSaved) != WorkflowMutationKind.Applied)
        {
            LastError = WorkflowMutations.Describe(scriptSaved);
            Status = "保存示例脚本失败";
            SaveState = SettingsSaveState.Failed;
            return false;
        }

        await RefreshAsync();
        SelectedWorkflow = Workflows.FirstOrDefault(item => item.Id == id);
        await WaitForEditorLoadAsync();
        Status = $"已新建「{created.Name}」，已填入可运行的最小回复示例。";
        return true;
    }

    /// <summary>Deletes the selected workflow. Locked and built-in definitions are refused.</summary>
    internal async Task<bool> DeleteAsync()
    {
        if (SelectedWorkflow is not { } selected)
        {
            LastError = "先在列表中选择一个工作流。";
            Status = "删除失败";
            SaveState = SettingsSaveState.Failed;
            return false;
        }

        if (selected.IsLocked || selected.IsBuiltIn)
        {
            LastError = DeleteExplanation;
            Status = "删除失败";
            SaveState = SettingsSaveState.Failed;
            return false;
        }

        WorkflowMutationResult result = await selected.Store.DeleteAsync(selected.Id, CancellationToken.None);
        if (WorkflowMutations.KindOf(result) != WorkflowMutationKind.Applied)
        {
            LastError = WorkflowMutations.Describe(result);
            Status = "删除失败";
            SaveState = SettingsSaveState.Failed;
            return false;
        }

        string name = selected.Name;
        await RefreshAsync();
        Status = $"已删除工作流「{name}」。";
        return true;
    }

    /// <summary>Re-reads the selected definition after a successful save (locked flags and badges).</summary>
    internal async Task ReloadSelectionAsync(string workflowId)
    {
        WorkflowStore store = await ResolveStoreAsync();
        FSharpOption<WorkflowDefinition>? loaded = await store.TryLoadAsync(workflowId, CancellationToken.None);
        WorkflowDefinitionItemViewModel? item = Workflows.FirstOrDefault(candidate => candidate.Id == workflowId);
        if (loaded is not null && item is not null)
        {
            item.ApplyDefinition(loaded.Value);
        }

        Raise(nameof(SelectedSummary));
        Raise(nameof(CanDeleteSelected));
    }

    internal async Task<WorkflowStore> ResolveStoreAsync()
    {
        if (_store is not null)
        {
            return _store;
        }

        string path = _libraryPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            HostServices services = await _main.ServicesAsync(false);
            path = services.RuntimeDatabasePath;
        }

        _store = WorkflowStore.ForLibrary(path);
        _configurationService = new WorkflowConfigurationService(_store, () => _main.AppOptions.Llm);
        Editor.UseStore(_store);
        return _store;
    }

    [ExcludeFromDerivedGeneration]
    internal WorkflowConfigurationService Configuration
    {
        get
        {
            WorkflowStore store = _store ?? throw new InvalidOperationException("工作流目录尚未就绪。");
            return _configurationService ??= new WorkflowConfigurationService(store, () => _main.AppOptions.Llm);
        }
    }

    [ExcludeFromDerivedGeneration] internal MainWindowViewModel Main => _main;

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkflowEditorViewModel.IsSaving) or nameof(WorkflowEditorViewModel.CanEdit))
        {
            Raise(nameof(CanDeleteSelected));
            Raise(nameof(CanCopySelected));
            CopyCommand.NotifyCanExecuteChanged();
            (DeleteCommand as IRelayCommand).NotifyCanExecuteChanged();
        }
    }

    /// <summary>Mints a free workflow id: the preferred id, or its first free numeric suffix.</summary>
    private static async Task<string> NextAvailableIdAsync(WorkflowStore store, string preferred)
    {
        for (int suffix = 1; suffix <= 999; suffix++)
        {
            string candidate = suffix == 1
                ? preferred
                : preferred + "." + suffix.ToString(CultureInfo.InvariantCulture);
            FSharpOption<WorkflowDefinition>? existing = await store.TryLoadAsync(candidate, CancellationToken.None);
            if (existing is null)
            {
                return candidate;
            }
        }

        return preferred + "." + Guid.NewGuid().ToString("N")[..8];
    }
}

/// <summary>How a <see cref="WorkflowMutationResult" /> case is classified for the UI.</summary>
internal enum WorkflowMutationKind
{
    Applied,
    Invalid,
    Locked,
    NotFound
}

/// <summary>Classification and rendering of store mutation results (the F# result is a union).</summary>
internal static class WorkflowMutations
{
    public static WorkflowMutationKind KindOf(WorkflowMutationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result switch
        {
            { IsApplied: true } => WorkflowMutationKind.Applied,
            { IsInvalid: true } => WorkflowMutationKind.Invalid,
            { IsLocked: true } => WorkflowMutationKind.Locked,
            _ => WorkflowMutationKind.NotFound
        };
    }

    /// <summary>A user-facing sentence for a refused or failed mutation.</summary>
    public static string Describe(WorkflowMutationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result switch
        {
            WorkflowMutationResult.Invalid invalid =>
                "工作流定义无效：" + string.Join(" ", invalid.problems ?? []),
            WorkflowMutationResult.Locked locked =>
                $"工作流「{locked.workflowId}」已锁定（内置工作流始终锁定），不允许修改或删除。",
            WorkflowMutationResult.NotFound notFound =>
                $"找不到工作流「{notFound.workflowId}」。",
            _ => "工作流保存失败。"
        };
    }
}

/// <summary>One row of the workflow list: identity, badges and the menu placement.</summary>
public sealed partial class WorkflowDefinitionItemViewModel : ViewModelBase
{
    private readonly WorkflowSettingsViewModel _section;
    private WorkflowDefinition _definition;

    internal WorkflowDefinitionItemViewModel(WorkflowSettingsViewModel section, WorkflowStore store,
        WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(definition);

        _section = section;
        Store = store;
        _definition = definition;
        SelectCommand = new RelayCommand(_ => _section.Select(this));
        OpenEditorCommand = new AsyncCommand(() => _section.OpenEditorAsync(this));
    }

    /// <summary>Selects this row in the editor.</summary>
    public RelayCommand SelectCommand { get; }

    public AsyncCommand OpenEditorCommand { get; }

    /// <summary>The store this definition lives in; every save/delete of the row goes through it.</summary>
    [ExcludeFromDerivedGeneration]
    public WorkflowStore Store { get; }

    [ExcludeFromDerivedGeneration] public WorkflowDefinition Source => _definition;

    [ExcludeFromDerivedGeneration] public bool IsBuiltIn => _definition.BuiltIn;

    [ExcludeFromDerivedGeneration] public bool IsLocked => _definition.Locked;

    [ExcludeFromDerivedGeneration] public string Id => _definition.Id;

    [ExcludeFromDerivedGeneration] public string Name => _definition.Name;

    [ExcludeFromDerivedGeneration] public string Description => _definition.Description;

    /// <summary>Badge shown for the definitions Patchouli ships.</summary>
    [ExcludeFromDerivedGeneration]
    public bool ShowBuiltInBadge => _definition.BuiltIn;

    /// <summary>Badge shown when the definition can neither be edited nor deleted.</summary>
    [ExcludeFromDerivedGeneration]
    public bool ShowLockedBadge => _definition.Locked;

    /// <summary>True while this row is the one the editor is bound to.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !ReferenceEquals(_section.SelectedWorkflow, this))
        {
            _section.Select(this);
        }
    }

    [ExcludeFromDerivedGeneration]
    public string MenuPlacementText => _definition.Menu.ShowInMenu
        ? $"菜单：{_definition.Menu.MenuPath} #{_definition.Menu.Order}"
        : "不在菜单中显示";

    [ExcludeFromDerivedGeneration] public string ScopeText => WorkflowScope.Describe(_definition.SelectionScope);

    /// <summary>Why the row's edit and delete actions are refused, empty when they are available.</summary>
    [ExcludeFromDerivedGeneration]
    public string LockReason => _definition switch
    {
        { BuiltIn: true } => "内置工作流：锁定，不可修改或删除。",
        { Locked: true } => "已锁定：不可修改或删除。",
        _ => ""
    };

    internal void ApplyDefinition(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
        Raise(nameof(Source));
        Raise(nameof(IsBuiltIn));
        Raise(nameof(IsLocked));
        Raise(nameof(Id));
        Raise(nameof(Name));
        Raise(nameof(Description));
        Raise(nameof(ShowBuiltInBadge));
        Raise(nameof(ShowLockedBadge));
        Raise(nameof(MenuPlacementText));
        Raise(nameof(ScopeText));
        Raise(nameof(LockReason));
    }
}

/// <summary>Rendering helpers for the <see cref="WorkflowSelectionScope" /> flag set.</summary>
internal static class WorkflowScope
{
    public static string Describe(WorkflowSelectionScope scope)
    {
        List<string> parts = [];
        if (scope.HasFlag(WorkflowSelectionScope.Documents))
        {
            parts.Add("文档");
        }

        if (scope.HasFlag(WorkflowSelectionScope.Pages))
        {
            parts.Add("页范围");
        }

        if (scope.HasFlag(WorkflowSelectionScope.Selection))
        {
            parts.Add("文本选择");
        }

        return parts.Count == 0 ? "无选择范围" : "适用：" + string.Join("、", parts);
    }

    public static WorkflowSelectionScope Toggle(WorkflowSelectionScope scope, WorkflowSelectionScope flag, bool on)
    {
        WorkflowSelectionScope without = scope & ~flag;
        return on ? without | flag : without;
    }
}

/// <summary>
///     The editor of one workflow definition: metadata, declared parameters, selection scope, menu
///     placement and the script text. Saving compiles the script first and blocks on any error
///     (ADR 0036 trust model: check on save plus a prominent permission warning).
/// </summary>
public sealed partial class WorkflowEditorViewModel : ViewModelBase
{
    private readonly WorkflowSettingsViewModel _section;
    private readonly object _parameterFieldsSync = new();
    private WorkflowStore? _store;
    private WorkflowDefinition? _definition;
    private bool _suppressDraftTracking;
    private ScriptCheckResult? _scriptCheck;
    private WorkflowConfigurationSnapshot? _configuration;
    private WorkflowDeclarationAnalysis? _analysis;
    private WorkflowLaunchSelection? _launchSelection;
    private string _configurationScriptText = "";
    private readonly Dictionary<string, WorkflowEditorDraft> _drafts = new(StringComparer.Ordinal);
    private int _loadGeneration;
    private long _editRevision;
    private bool _isLoadingConfiguration;

    internal WorkflowEditorViewModel(WorkflowSettingsViewModel section, WorkflowStore? store)
    {
        ArgumentNullException.ThrowIfNull(section);
        _section = section;
        _store = store;
        SaveScriptCommand = new AsyncCommand(SaveAsync, () => CanSave);
        CheckScriptCommand = new AsyncCommand(CheckScriptAsync, () => CanEdit);
    }

    /// <summary>The accepted parameter type names, shown next to the parameter editor.</summary>
    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<WorkflowParameterTypeOption> TypeOptions => WorkflowParameterTypes.All;

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty] public partial string Description { get; set; } = "";

    [ObservableProperty] public partial string ScriptEntryPoint { get; set; } = "run";

    [ObservableProperty] public partial string MenuPath { get; set; } = "";

    [ObservableProperty] public partial int MenuOrder { get; set; }

    [ObservableProperty] public partial bool ShowInMenu { get; set; }

    [ObservableProperty] public partial bool ScopeDocuments { get; set; }

    [ObservableProperty] public partial bool ScopePages { get; set; }

    [ObservableProperty] public partial bool ScopeSelection { get; set; }

    /// <summary>One declared parameter per line: <c>name: Type | required | description | 默认=value</c>.</summary>
    [ObservableProperty]
    public partial string ParametersText { get; set; } = "";

    [ObservableProperty] public partial string ScriptText { get; set; } = "";

    /// <summary>Fields discovered in independent SDK Parameter bindings in the script.</summary>
    [ExcludeFromDerivedGeneration]
    public ObservableCollection<WorkflowParameterFieldViewModel> ParameterFields { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkflowInfo))]
    public partial string WorkflowInfoText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchContext))]
    public partial string LaunchContextText { get; set; } = "";

    [ExcludeFromDerivedGeneration] public bool HasLaunchContext => !string.IsNullOrWhiteSpace(LaunchContextText);

    [ExcludeFromDerivedGeneration] public bool HasWorkflowInfo => !string.IsNullOrWhiteSpace(WorkflowInfoText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScopeIssue))]
    public partial string? ScopeIssue { get; set; }

    [ExcludeFromDerivedGeneration] public bool HasScopeIssue => !string.IsNullOrWhiteSpace(ScopeIssue);

    internal void SetLaunchContext(WorkflowLaunchSelection? selection)
    {
        VerifyUiThread();
        lock (_parameterFieldsSync)
        {
            _launchSelection = selection;
        }

        ApplyLaunchContextToFields();
    }

    private void ApplyLaunchContextToFields()
    {
        VerifyUiThread();
        lock (_parameterFieldsSync)
        {
            WorkflowLaunchSelection? selection = _launchSelection;
            if (selection is null)
            {
                LaunchContextText = "";
                return;
            }

            List<string> values = [];
            if (!string.IsNullOrWhiteSpace(selection.DocumentId))
            {
                values.Add($"文档：{selection.DocumentId}");
            }

            if (!string.IsNullOrWhiteSpace(selection.PageRange))
            {
                values.Add($"页码范围：{selection.PageRange}");
            }

            if (!string.IsNullOrWhiteSpace(selection.TextSelection))
            {
                values.Add($"选中文本：{selection.TextSelection}");
            }

            if (selection.DocumentIds.Count > 1)
            {
                values.Add($"已选文档：{selection.DocumentIds.Count} 个");
            }

            LaunchContextText = values.Count == 0 ? "当前没有文档或页码上下文。" : string.Join(" · ", values);
            foreach (WorkflowParameterFieldViewModel field in ParameterFields)
            {
                string? value = field.ContextBinding switch
                {
                    WorkflowSessionRunner.DocumentIdParameter => selection.DocumentId,
                    "documents" => JsonSerializer.Serialize(selection.DocumentIds),
                    WorkflowSessionRunner.PageRangeParameter => selection.PageRange,
                    "textSelection" => selection.TextSelection,
                    _ => null
                };
                field.SetEffectiveContext(value);
            }
        }
    }

    public AsyncCommand SaveScriptCommand { get; }
    public AsyncCommand CheckScriptCommand { get; }

    /// <summary>Structured compile diagnostics of the current script text, in compiler order.</summary>
    [ExcludeFromDerivedGeneration]
    public ObservableCollection<WorkflowScriptDiagnosticViewModel> Diagnostics { get; } = [];

    /// <summary>The result line of the last check or save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string ResultText { get; set; } = "";

    /// <summary>True when the last check or save failed; the result panel then renders as an error.</summary>
    [ObservableProperty]
    public partial bool ResultIsError { get; set; }

    [ExcludeFromDerivedGeneration] public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);

    /// <summary>True when the definition is a stored, unlocked workflow.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    [NotifyPropertyChangedFor(nameof(CanEditFields))]
    [NotifyPropertyChangedFor(nameof(LockExplanation))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool CanEdit { get; set; }

    /// <summary>True while the editor is persisting; saving and deleting are disabled meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanEditFields))]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsConfigurationDirty { get; set; }

    /// <summary>True while the draft differs from the stored definition or script.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsDirty { get; set; }

    [ExcludeFromDerivedGeneration] public bool IsReadOnly => !CanEdit;

    [ExcludeFromDerivedGeneration] public bool CanEditFields => _definition is not null && !IsSaving;

    [ExcludeFromDerivedGeneration]
    public string LockExplanation => _definition switch
    {
        null => "先在列表中选择一个工作流。",
        { BuiltIn: true } => "内置工作流（锁定）：不允许修改或删除。要从它的脚本开始，请使用「从内置工作流复制」。",
        { Locked: true } => "该工作流已锁定，不允许修改或删除。",
        _ => ""
    };

    [ExcludeFromDerivedGeneration]
    public string HeaderText => _definition is { } definition
        ? $"{definition.Name}（{definition.Id}）"
        : "未选择工作流";

    /// <summary>The prominent .fsx permission warning (plan §3.2).</summary>
    [ExcludeFromDerivedGeneration]
    public string TrustWarning => WorkflowSettingsViewModel.TrustWarning;

    [ExcludeFromDerivedGeneration]
    public string ScriptHint =>
        "保存前先做 F# 编译检查：有错误的脚本不会写入磁盘。脚本入口点在执行时按名称调用。";

    /// <summary>The accepted parameter type names, shown under the parameter editor.</summary>
    [ExcludeFromDerivedGeneration]
    public string ParameterTypesHint =>
        "类型：" + string.Join("、", WorkflowParameterTypes.All.Select(option => option.DisplayName)) +
        "（对应 Text / DocumentId / PageRange / Language / Integer / Boolean）。";

    [ExcludeFromDerivedGeneration] public bool HasDiagnostics => Diagnostics.Count > 0;

    [ExcludeFromDerivedGeneration]
    public string DiagnosticsSummary => Diagnostics.Count == 0 ? "" : $"编译诊断 {Diagnostics.Count} 条";

    /// <summary>True when the last compile check of the current text produced no error.</summary>
    [ExcludeFromDerivedGeneration]
    public bool LastCheckSucceeded => _scriptCheck is { Succeeded: true };

    [ExcludeFromDerivedGeneration]
    public bool CanSave =>
        !IsSaving && ((CanEdit && IsDirty) || IsConfigurationDirty);

    /// <summary>Points a host-resolved editor at the store the section discovered.</summary>
    internal void UseStore(WorkflowStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Binds the editor to one definition and reads its script text.</summary>
    public async Task LoadAsync(WorkflowDefinitionItemViewModel? item)
    {
        VerifyUiThread();
        if (_definition is { } previous && (IsDirty || IsConfigurationDirty))
        {
            _drafts[previous.Id] = CaptureDraft();
        }

        // Every load gets its own generation. Clearing the editor happens synchronously, so a load
        // that has been superseded in the meantime must abort instead of wiping the newer draft.
        int generation;
        lock (_parameterFieldsSync)
        {
            generation = ++_loadGeneration;
        }

        WorkflowDefinition? definition = item?.Source;

        // Binding the editor is not a user edit: every assignment below is suppressed so the section
        // never reports a draft that came from loading.
        SetDraftTrackingSuppressed(true);
        try
        {
            _definition = definition;
            if (item is not null)
            {
                _store = item.Store;
            }

            WorkflowStore? store = _store;
            CanEdit = definition is { Locked: false, BuiltIn: false };
            Name = definition?.Name ?? "";
            Description = definition?.Description ?? "";
            ScriptEntryPoint = definition?.ScriptEntryPoint ?? "run";
            MenuPath = definition?.Menu.MenuPath ?? WorkflowDefinitions.DefaultMenuPath;
            MenuOrder = definition?.Menu.Order ?? 100;
            ShowInMenu = definition?.Menu.ShowInMenu ?? true;
            WorkflowSelectionScope scope = definition?.SelectionScope ?? WorkflowSelectionScope.All;
            ScopeDocuments = scope.HasFlag(WorkflowSelectionScope.Documents);
            ScopePages = scope.HasFlag(WorkflowSelectionScope.Pages);
            ScopeSelection = scope.HasFlag(WorkflowSelectionScope.Selection);
            ParametersText = FormatParameters(definition?.Parameters);
            ScriptText = "";
            WorkflowInfoText = "";
            ScopeIssue = null;
            _configuration = null;
            _analysis = null;
            lock (_parameterFieldsSync)
            {
                ParameterFields.Clear();
            }

            IsConfigurationDirty = false;
            ClearDiagnostics();
            ResultText = "";
            ResultIsError = false;
            Raise(nameof(HeaderText));
            Raise(nameof(LockExplanation));
            Raise(nameof(IsReadOnly));

            if (definition is not null && store is not null)
            {
                // The suppress flag must not stay set across the await: a user edit that lands while
                // the script is being read has to mark the section dirty and supersede this load.
                SetDraftTrackingSuppressed(false);
                FSharpOption<string>? script = await store.ReadScriptAsync(definition.Id, CancellationToken.None);
                if (!IsCurrentLoad(generation))
                {
                    return;
                }

                SetDraftTrackingSuppressed(true);
                if (script is null)
                {
                    ResultText = "该工作流还没有脚本文本。";
                    ResultIsError = definition.BuiltIn;
                }
                else
                {
                    ScriptText = script.Value;
                    await LoadConfigurationAsync(definition.Id, script.Value, generation);
                }
            }
        }
        finally
        {
            SetDraftTrackingSuppressed(false);
        }

        if (!IsCurrentLoad(generation))
        {
            return;
        }

        if (definition is not null && _drafts.Remove(definition.Id, out WorkflowEditorDraft? draft) &&
            draft is not null)
        {
            SetDraftTrackingSuppressed(true);
            try
            {
                ScriptText = draft.ScriptText;
                IsDirty = draft.IsScriptDirty;
                if (!string.Equals(draft.ScriptText, _configurationScriptText, StringComparison.Ordinal))
                {
                    await LoadConfigurationAsync(definition.Id, draft.ScriptText, generation);
                }

                RestoreParameterValues(draft.Values);
                IsConfigurationDirty = draft.IsConfigurationDirty;
            }
            finally
            {
                SetDraftTrackingSuppressed(false);
            }
        }
        else
        {
            IsDirty = false;
            IsConfigurationDirty = false;
        }

        NotifyCommands();
    }

    /// <summary>Restores the editor from the definition it is bound to, dropping every draft.</summary>
    public void ResetToSelected()
    {
        if (_definition is { } current)
        {
            _drafts.Remove(current.Id);
        }

        IsDirty = false;
        IsConfigurationDirty = false;
        _ = LoadAsync(_section.SelectedWorkflow);
    }

    public async Task DiscardDraftAsync()
    {
        if (_definition is { } current)
        {
            _drafts.Remove(current.Id);
        }

        IsDirty = false;
        IsConfigurationDirty = false;
        await LoadAsync(_section.SelectedWorkflow);
    }

    private async Task LoadConfigurationAsync(string workflowId, string scriptText, int generation)
    {
        long revision;
        lock (_parameterFieldsSync)
        {
            revision = _editRevision;
        }

        WorkflowConfigurationSnapshot snapshot = await _section.Configuration.ReadAsync(
            workflowId, scriptText, null, CancellationToken.None);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyConfigurationSnapshot(snapshot, null, generation, revision));
    }

    private bool ApplyConfigurationSnapshot(WorkflowConfigurationSnapshot snapshot,
        IReadOnlyDictionary<string, string>? overrides, int? expectedGeneration = null,
        long? expectedRevision = null, WorkflowDefinition? expectedDefinition = null)
    {
        VerifyUiThread();
        lock (_parameterFieldsSync)
        {
            if ((expectedGeneration.HasValue && expectedGeneration.Value != _loadGeneration) ||
                (expectedRevision.HasValue && expectedRevision.Value != _editRevision) ||
                (expectedDefinition is not null && !ReferenceEquals(expectedDefinition, _definition)))
            {
                return false;
            }

            _configuration = snapshot;
            _analysis = snapshot.Analysis;
            WorkflowDeclarationAnalysis analysis = snapshot.Analysis;
            WorkflowDeclarationInfo info = analysis.Info;
            WorkflowInfoText = $"{info.Name}\n{info.Description}\n入口：{info.EntryPoint} · 菜单：{info.MenuPath} " +
                               $"({info.MenuOrder}) · 范围：{info.SelectionScope}";

            _isLoadingConfiguration = true;
            try
            {
                ParameterFields.Clear();
                foreach (ParameterDescriptor descriptor in analysis.Fields)
                {
                    snapshot.Values.TryGetValue(descriptor.Key, out string? value);
                    if (overrides?.TryGetValue(descriptor.Key, out string? draftValue) == true)
                    {
                        value = draftValue;
                    }

                    WorkflowParameterFieldViewModel field = new(
                        descriptor, value,
                        descriptor.Type == WorkflowParameterValueType.Model ? BuildModelOptions() : null,
                        OnParameterFieldChanged);
                    field.IsEnabled = CanEditFields && !field.IsContextBound;
                    ParameterFields.Add(field);
                }

                foreach (WorkflowValidationIssue issue in snapshot.Issues)
                {
                    if (!string.IsNullOrWhiteSpace(issue.Key))
                    {
                        WorkflowParameterFieldViewModel? field =
                            ParameterFields.FirstOrDefault(candidate => candidate.Key == issue.Key);
                        if (field is not null)
                        {
                            field.SetValidationError(issue.Message);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(issue.ScopeTarget))
                    {
                        ScopeIssue = issue.Message;
                    }
                    else
                    {
                        ResultText = issue.Message;
                        ResultIsError = true;
                    }
                }
            }
            finally
            {
                _isLoadingConfiguration = false;
            }

            if (expectedGeneration.HasValue)
            {
                _configurationScriptText = ScriptText;
            }

            ApplyLaunchContextToFields();
        }

        Raise(nameof(HasWorkflowInfo));
        return true;
    }

    private bool IsCurrentLoad(int generation)
    {
        lock (_parameterFieldsSync)
        {
            return generation == _loadGeneration;
        }
    }

    private bool IsCurrentRevision(long revision)
    {
        lock (_parameterFieldsSync)
        {
            return revision == _editRevision;
        }
    }

    private void SetDraftTrackingSuppressed(bool value)
    {
        lock (_parameterFieldsSync)
        {
            _suppressDraftTracking = value;
        }
    }

    private static void VerifyUiThread()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException("工作流参数表单只能在 Avalonia UI 线程上访问。");
        }
    }

    private Dictionary<string, string> CaptureParameterValues()
    {
        VerifyUiThread();
        lock (_parameterFieldsSync)
        {
            return ParameterFields.ToDictionary(
                field => field.Key, field => field.Value, StringComparer.Ordinal);
        }
    }

    private async Task RefreshConfigurationForDraftAsync()
    {
        VerifyUiThread();
        WorkflowDefinition? definition = _definition;
        if (definition is null || string.IsNullOrWhiteSpace(ScriptText))
        {
            return;
        }

        int generation;
        long revision;
        string scriptText = ScriptText;
        Dictionary<string, string> overrides;
        lock (_parameterFieldsSync)
        {
            generation = _loadGeneration;
            revision = _editRevision;
            overrides = ParameterFields.ToDictionary(
                field => field.Key, field => field.Value, StringComparer.Ordinal);
        }

        try
        {
            WorkflowConfigurationSnapshot snapshot = await ReadConfigurationAsync(definition.Id, scriptText);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_parameterFieldsSync)
                {
                    if (!ApplyConfigurationSnapshot(snapshot, overrides, generation, revision, definition))
                    {
                        return;
                    }

                    IsConfigurationDirty = !ParameterFields.All(candidate =>
                        snapshot.Values.TryGetValue(candidate.Key, out string? saved)
                            ? string.Equals(candidate.Value, saved, StringComparison.Ordinal)
                            : candidate.Descriptor.HasDefault
                                ? string.Equals(candidate.Value, candidate.Descriptor.DefaultValue,
                                    StringComparison.Ordinal)
                                : string.IsNullOrEmpty(candidate.Value));
                }
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_parameterFieldsSync)
                {
                    if (!IsCurrentLoad(generation) || !IsCurrentRevision(revision) ||
                        !ReferenceEquals(definition, _definition))
                    {
                        return;
                    }

                    ResultText = $"参数声明分析失败：{exception.Message}";
                    ResultIsError = true;
                }
            });
        }
    }

    private IEnumerable<WorkflowModelOption> BuildModelOptions()
    {
        LlmSettingsViewModel settings = _section.Main.Settings.LlmSettings;
        foreach (LlmProviderSettingsRowViewModel provider in settings.Providers)
        {
            HashSet<string> models = new(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(provider.Model))
            {
                models.Add(provider.Model.Trim());
            }

            foreach (string model in provider.SubscriptionModels)
            {
                if (!string.IsNullOrWhiteSpace(model))
                {
                    models.Add(model.Trim());
                }
            }

            foreach (string model in models)
            {
                yield return new WorkflowModelOption(provider.DisplayName, provider.ProviderId, model);
            }

            if (models.Count == 0)
            {
                yield return new WorkflowModelOption(provider.DisplayName, provider.ProviderId, "");
            }
        }
    }

    private void OnParameterFieldChanged(WorkflowParameterFieldViewModel field)
    {
        VerifyUiThread();
        lock (_parameterFieldsSync)
        {
            if (_isLoadingConfiguration || _suppressDraftTracking)
            {
                return;
            }

            _editRevision++;
            IsConfigurationDirty = !ParameterFields.All(candidate =>
                _configuration?.Values.TryGetValue(candidate.Key, out string? saved) == true
                    ? string.Equals(candidate.Value, saved, StringComparison.Ordinal)
                    : candidate.Descriptor.HasDefault
                        ? string.Equals(candidate.Value, candidate.Descriptor.DefaultValue, StringComparison.Ordinal)
                        : string.IsNullOrEmpty(candidate.Value));
        }

        NotifyCommands();
    }

    private WorkflowEditorDraft CaptureDraft()
    {
        VerifyUiThread();
        lock (_parameterFieldsSync)
        {
            return new WorkflowEditorDraft(ScriptText, IsDirty,
                ParameterFields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal),
                IsConfigurationDirty);
        }
    }

    private void RestoreParameterValues(IReadOnlyDictionary<string, string> values)
    {
        VerifyUiThread();
        lock (_parameterFieldsSync)
        {
            _isLoadingConfiguration = true;
            try
            {
                foreach (WorkflowParameterFieldViewModel field in ParameterFields)
                {
                    if (values.TryGetValue(field.Key, out string? value))
                    {
                        field.Value = value;
                    }
                }
            }
            finally
            {
                _isLoadingConfiguration = false;
            }
        }
    }

    internal void SetValidationIssues(IReadOnlyList<WorkflowValidationIssue> issues)
    {
        VerifyUiThread();
        lock (_parameterFieldsSync)
        {
            ScopeIssue = null;
            WorkflowValidationIssue? unlocatedIssue = null;
            foreach (WorkflowParameterFieldViewModel field in ParameterFields)
            {
                field.ErrorMessage = null;
            }

            foreach (WorkflowValidationIssue issue in issues)
            {
                if (!string.IsNullOrWhiteSpace(issue.Key))
                {
                    WorkflowParameterFieldViewModel? field =
                        ParameterFields.FirstOrDefault(candidate => candidate.Key == issue.Key);
                    if (field is not null)
                    {
                        field.SetValidationError(issue.Message);
                    }
                    else
                    {
                        unlocatedIssue ??= issue;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(issue.ScopeTarget))
                {
                    ScopeIssue = issue.Message;
                }
                else
                {
                    unlocatedIssue ??= issue;
                }
            }

            if (unlocatedIssue is not null)
            {
                ResultText = unlocatedIssue.Message;
                ResultIsError = true;
            }
        }
    }

    /// <summary>
    ///     Validates the definition, compiles the script and only then writes definition and script.
    ///     Any compile error blocks the save; the diagnostics carry line, column and message.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        VerifyUiThread();
        // A selection change loads the script in the background; never let that read land on top of
        // the draft the user is saving.
        await _section.WaitForEditorLoadAsync();

        WorkflowStore? store = _store;
        WorkflowDefinition? definition = _definition;
        if (store is null || definition is null)
        {
            ResultText = "先在列表中选择一个工作流。";
            ResultIsError = true;
            return false;
        }

        if (!CanEdit && !IsConfigurationDirty)
        {
            ResultText = LockExplanation;
            ResultIsError = true;
            return false;
        }

        IsSaving = true;
        NotifyCommands();
        try
        {
            bool saveScript = CanEdit && IsDirty;
            WorkflowConfigurationSnapshot snapshot = _configuration is not null &&
                                                     string.Equals(_configurationScriptText, ScriptText,
                                                         StringComparison.Ordinal)
                ? _configuration
                : await ReadConfigurationAsync(definition.Id, ScriptText);

            if (saveScript)
            {
                // The compile and declaration checks are the gate: invalid fsx is never written.
                ScriptCheckResult check = snapshot.Analysis.Succeeded
                    ? await CheckScriptWithAsync(store, ScriptText, definition.Id,
                        snapshot.Analysis.Info.EntryPoint)
                    : new ScriptCheckResult(false, snapshot.Analysis.Diagnostics);
                ShowDiagnostics(check);
                if (!check.Succeeded || !snapshot.Analysis.Succeeded)
                {
                    bool hasFSharpCompilerError = snapshot.Analysis.Diagnostics.Concat(check.Diagnostics)
                        .Any(diagnostic => diagnostic.Severity == ScriptDiagnosticSeverity.Error &&
                                           diagnostic.ErrorNumber.StartsWith("FS", StringComparison.Ordinal));
                    if (snapshot.Issues.Count > 0)
                    {
                        SetValidationIssues(snapshot.Issues);
                        string details = string.Join("\n", snapshot.Issues.Select(issue => issue.Message));
                        ResultText = hasFSharpCompilerError
                            ? $"脚本编译检查失败：{details}"
                            : $"脚本声明检查失败：{details}";
                    }
                    else
                    {
                        ResultText = "脚本或参数声明未通过检查，未保存更改。请修正诊断后重试。";
                    }

                    ResultIsError = true;
                    return false;
                }

                WorkflowDefinition draft =
                    WorkflowConfigurationService.ProjectDefinition(definition, snapshot.Analysis);
                WorkflowValidation validation = WorkflowDefinitions.validate(draft);
                if (!validation.IsValid)
                {
                    ResultText = string.Join(" ", validation.Problems ?? []);
                    ResultIsError = true;
                    return false;
                }

                WorkflowMutationResult scriptSaved =
                    await store.SaveScriptAsync(definition.Id, ScriptText, CancellationToken.None);
                if (WorkflowMutations.KindOf(scriptSaved) != WorkflowMutationKind.Applied)
                {
                    ResultText = WorkflowMutations.Describe(scriptSaved);
                    ResultIsError = true;
                    return false;
                }

                await _section.Configuration.SaveDeclarationProjectionAsync(
                    definition.Id, snapshot.Analysis, CancellationToken.None);
                _definition = draft;
                _configuration = snapshot;
                _analysis = snapshot.Analysis;
                _configurationScriptText = ScriptText;
            }

            if (IsConfigurationDirty)
            {
                IReadOnlyDictionary<string, string> values = CaptureParameterValues();
                WorkflowConfigurationSaveResult savedConfiguration =
                    await _section.Configuration.SaveAsync(
                        definition.Id, snapshot.DeclarationFingerprint, values, CancellationToken.None);
                if (!savedConfiguration.Saved)
                {
                    ResultText = string.Join(" ", savedConfiguration.Issues.Select(issue => issue.Message));
                    ResultIsError = true;
                    SetValidationIssues(savedConfiguration.Issues);
                    return false;
                }

                _configuration = await ReadConfigurationAsync(definition.Id, ScriptText);
                _configurationScriptText = ScriptText;
                IsConfigurationDirty = false;
                _drafts.Remove(definition.Id);
            }

            IsDirty = false;
            ResultText = saveScript ? "已保存工作流脚本与配置。" : "已保存工作流配置。";
            ResultIsError = false;
            if (saveScript)
            {
                await _section.ReloadSelectionAsync(definition.Id);
            }

            return true;
        }
        finally
        {
            IsSaving = false;
            NotifyCommands();
        }
    }

    private async Task<WorkflowConfigurationSnapshot> ReadConfigurationAsync(string workflowId, string scriptText)
    {
        return await _section.Configuration.ReadAsync(
            workflowId, scriptText, null, CancellationToken.None);
    }

    /// <summary>Compiles the current script text without writing anything.</summary>
    public async Task<bool> CheckScriptAsync()
    {
        VerifyUiThread();
        await _section.WaitForEditorLoadAsync();

        WorkflowStore? store = _store;
        WorkflowDefinition? definition = _definition;
        if (store is null || definition is null)
        {
            ResultText = "先在列表中选择一个工作流。";
            ResultIsError = true;
            return false;
        }

        int generation;
        long revision;
        string scriptText = ScriptText;
        Dictionary<string, string> overrides;
        lock (_parameterFieldsSync)
        {
            generation = _loadGeneration;
            revision = _editRevision;
            overrides = ParameterFields.ToDictionary(
                field => field.Key, field => field.Value, StringComparer.Ordinal);
        }

        WorkflowConfigurationSnapshot configuration = await ReadConfigurationAsync(definition.Id, scriptText);
        if (!configuration.Analysis.Succeeded || configuration.Issues.Count > 0)
        {
            bool applied = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_parameterFieldsSync)
                {
                    if (!ApplyConfigurationSnapshot(configuration, overrides, generation, revision, definition))
                    {
                        return false;
                    }

                    ResultText = string.Join("\n", configuration.Issues.Select(issue => issue.Message));
                    ResultIsError = true;
                    return true;
                }
            });
            if (!applied)
            {
                return false;
            }

            return false;
        }

        ScriptCheckResult check = await CheckScriptWithAsync(
            store, scriptText, definition.Id, configuration.Analysis.Info.EntryPoint);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (_parameterFieldsSync)
            {
                if (!IsCurrentLoad(generation) || !IsCurrentRevision(revision))
                {
                    return false;
                }

                ShowDiagnostics(check);
                ResultText = check.Succeeded ? "编译检查通过，可以保存。" : "编译检查发现错误，保存已被阻止。";
                ResultIsError = !check.Succeeded;
                return check.Succeeded;
            }
        });
    }

    /// <summary>
    ///     Runs the real F# compile check of one script text. It is the same service the executor
    ///     uses, so a passing check means the script type-checks in a run.
    /// </summary>
    internal async Task<ScriptCheckResult> CheckScriptWithAsync(WorkflowStore store, string scriptText,
        string workflowId, string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return await _section.Compiler.CheckWorkflowAsync(scriptText, store.ResolveScriptPath(workflowId),
                entryPoint);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ScriptCheckResult(false,
            [
                new ScriptDiagnostic(workflowId, 0, 0, 0, 0, ScriptDiagnosticSeverity.Error, "",
                    $"编译检查无法运行：{exception.Message}")
            ]);
        }
    }

    private void NotifyCommands()
    {
        (SaveScriptCommand as IRelayCommand).NotifyCanExecuteChanged();
        (CheckScriptCommand as IRelayCommand).NotifyCanExecuteChanged();
    }

    private void ShowDiagnostics(ScriptCheckResult check)
    {
        _scriptCheck = check;
        Diagnostics.Clear();
        foreach (ScriptDiagnostic diagnostic in check.Diagnostics ?? [])
        {
            Diagnostics.Add(new WorkflowScriptDiagnosticViewModel(diagnostic));
        }

        Raise(nameof(HasDiagnostics));
        Raise(nameof(DiagnosticsSummary));
        Raise(nameof(LastCheckSucceeded));
    }

    private void ClearDiagnostics()
    {
        _scriptCheck = null;
        Diagnostics.Clear();
        Raise(nameof(HasDiagnostics));
        Raise(nameof(DiagnosticsSummary));
        Raise(nameof(LastCheckSucceeded));
    }

    private WorkflowSelectionScope CurrentScope()
    {
        WorkflowSelectionScope scope = WorkflowScope.Toggle(WorkflowSelectionScope.Nothing,
            WorkflowSelectionScope.Documents, ScopeDocuments);
        scope = WorkflowScope.Toggle(scope, WorkflowSelectionScope.Pages, ScopePages);
        return WorkflowScope.Toggle(scope, WorkflowSelectionScope.Selection, ScopeSelection);
    }

    private static string FormatParameters(WorkflowParameter[]? parameters)
    {
        if (parameters is null || parameters.Length == 0)
        {
            return "";
        }

        StringBuilder builder = new();
        foreach (WorkflowParameter parameter in parameters)
        {
            builder.Append(parameter.Name).Append(": ").Append(WorkflowParameterTypes.NameOf(parameter.Type));
            if (parameter.Required)
            {
                builder.Append(" | required");
            }

            if (!string.IsNullOrWhiteSpace(parameter.Description))
            {
                builder.Append(" | ").Append(parameter.Description.Trim());
            }

            if (!string.IsNullOrWhiteSpace(parameter.DefaultValue))
            {
                builder.Append(" | 默认=").Append(parameter.DefaultValue.Trim());
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Parses one parameter per line: <c>name: Type | required | description | 默认=value</c>. A
    ///     missing or unknown type falls back to text; an unusable line is reported verbatim.
    /// </summary>
    internal static bool TryParseParameters(string text, out WorkflowParameter[] parameters, out string? problem)
    {
        parameters = [];
        problem = null;
        List<WorkflowParameter> parsed = [];
        string[] lines = (text ?? "").ReplaceLineEndings("\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf(':', StringComparison.Ordinal);
            string name = (separator < 0 ? line : line[..separator]).Trim();
            if (name.Length == 0)
            {
                problem = $"参数第 {index + 1} 行缺少名称：{line}";
                return false;
            }

            string[] segments = separator < 0
                ? []
                : line[(separator + 1)..].Split('|', StringSplitOptions.TrimEntries);
            WorkflowParameterType type = WorkflowParameterType.Text;
            bool required = false;
            string description = "";
            string defaultValue = "";
            for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                string segment = segments[segmentIndex].Trim();
                if (segment.Length == 0)
                {
                    continue;
                }

                if (segmentIndex == 0 && WorkflowParameterTypes.TryParse(segment, out WorkflowParameterType declared))
                {
                    type = declared;
                    continue;
                }

                if (segment.Equals("required", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("必填", StringComparison.Ordinal))
                {
                    required = true;
                    continue;
                }

                if (segment.StartsWith("默认=", StringComparison.Ordinal))
                {
                    defaultValue = segment["默认=".Length..].Trim();
                    continue;
                }

                if (segment.StartsWith("default=", StringComparison.OrdinalIgnoreCase))
                {
                    defaultValue = segment["default=".Length..].Trim();
                    continue;
                }

                description = description.Length == 0 ? segment : description + " " + segment;
            }

            parsed.Add(new WorkflowParameter(name, type, required, description, defaultValue));
        }

        parameters = [.. parsed];
        return true;
    }

    partial void OnScriptTextChanged(string value)
    {
        ClearDiagnostics();
        ResultText = "";
        ResultIsError = false;
        MarkDirty();
        _ = RefreshConfigurationForDraftAsync();
    }

    partial void OnIsSavingChanged(bool value)
    {
        VerifyUiThread();
        lock (_parameterFieldsSync)
        {
            foreach (WorkflowParameterFieldViewModel field in ParameterFields)
            {
                field.IsEnabled = !value && !field.IsContextBound;
            }
        }
    }

    partial void OnNameChanged(string value)
    {
        MarkDirty();
    }

    partial void OnDescriptionChanged(string value)
    {
        MarkDirty();
    }

    partial void OnScriptEntryPointChanged(string value)
    {
        MarkDirty();
    }

    partial void OnMenuPathChanged(string value)
    {
        MarkDirty();
    }

    partial void OnMenuOrderChanged(int value)
    {
        MarkDirty();
    }

    partial void OnShowInMenuChanged(bool value)
    {
        MarkDirty();
    }

    partial void OnScopeDocumentsChanged(bool value)
    {
        MarkDirty();
    }

    partial void OnScopePagesChanged(bool value)
    {
        MarkDirty();
    }

    partial void OnScopeSelectionChanged(bool value)
    {
        MarkDirty();
    }

    partial void OnParametersTextChanged(string value)
    {
        MarkDirty();
    }

    private void MarkDirty()
    {
        lock (_parameterFieldsSync)
        {
            if (_suppressDraftTracking)
            {
                return;
            }

            // An unsaved edit supersedes every in-flight load: the load must not land on this draft.
            _editRevision++;
            _loadGeneration++;
        }

        IsDirty = true;
        NotifyCommands();
    }
}

internal sealed record WorkflowEditorDraft(
    string ScriptText,
    bool IsScriptDirty,
    IReadOnlyDictionary<string, string> Values,
    bool IsConfigurationDirty);

/// <summary>One compile diagnostic of a workflow script, rendered with line and column.</summary>
public sealed class WorkflowScriptDiagnosticViewModel
{
    internal WorkflowScriptDiagnosticViewModel(ScriptDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        Severity = diagnostic.Severity;
        Line = diagnostic.Line;
        Column = diagnostic.Column;
        ErrorNumber = diagnostic.ErrorNumber ?? "";
        Message = diagnostic.Message ?? "";
    }

    public ScriptDiagnosticSeverity Severity { get; }
    public int Line { get; }
    public int Column { get; }
    public string ErrorNumber { get; }
    public string Message { get; }

    public bool IsError => Severity == ScriptDiagnosticSeverity.Error;

    public string SeverityText => Severity switch
    {
        ScriptDiagnosticSeverity.Error => "错误",
        ScriptDiagnosticSeverity.Warning => "警告",
        _ => "信息"
    };

    public string LocationText => Line <= 0 ? "脚本" : $"第 {Line} 行, 第 {Column + 1} 列";

    public string DisplayText => $"{LocationText} [{SeverityText}] {ErrorNumber}：{Message}";
}

/// <summary>Parameter type choice of the parameter editor.</summary>
public sealed class WorkflowParameterTypeOption
{
    internal WorkflowParameterTypeOption(WorkflowParameterType type, string displayName)
    {
        Type = type;
        DisplayName = displayName;
    }

    public WorkflowParameterType Type { get; }
    public string DisplayName { get; }
}

/// <summary>Display names and aliases of the declared parameter types.</summary>
internal static class WorkflowParameterTypes
{
    /// <summary>Display order of the parameter types.</summary>
    public static IReadOnlyList<WorkflowParameterTypeOption> All { get; } =
    [
        new(WorkflowParameterType.Text, "文本"),
        new(WorkflowParameterType.DocumentId, "文档 id"),
        new(WorkflowParameterType.PageRange, "页范围"),
        new(WorkflowParameterType.Language, "语言"),
        new(WorkflowParameterType.Integer, "整数"),
        new(WorkflowParameterType.Boolean, "布尔")
    ];

    private static readonly Dictionary<string, WorkflowParameterType> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = WorkflowParameterType.Text,
            ["文本"] = WorkflowParameterType.Text,
            ["documentid"] = WorkflowParameterType.DocumentId,
            ["document"] = WorkflowParameterType.DocumentId,
            ["文档"] = WorkflowParameterType.DocumentId,
            ["pagerange"] = WorkflowParameterType.PageRange,
            ["pages"] = WorkflowParameterType.PageRange,
            ["页范围"] = WorkflowParameterType.PageRange,
            ["language"] = WorkflowParameterType.Language,
            ["语言"] = WorkflowParameterType.Language,
            ["integer"] = WorkflowParameterType.Integer,
            ["int"] = WorkflowParameterType.Integer,
            ["整数"] = WorkflowParameterType.Integer,
            ["boolean"] = WorkflowParameterType.Boolean,
            ["bool"] = WorkflowParameterType.Boolean,
            ["布尔"] = WorkflowParameterType.Boolean
        };

    public static string NameOf(WorkflowParameterType type)
    {
        return type switch
        {
            WorkflowParameterType.DocumentId => "DocumentId",
            WorkflowParameterType.PageRange => "PageRange",
            WorkflowParameterType.Language => "Language",
            WorkflowParameterType.Integer => "Integer",
            WorkflowParameterType.Boolean => "Boolean",
            _ => "Text"
        };
    }

    public static bool TryParse(string text, out WorkflowParameterType type)
    {
        return Aliases.TryGetValue((text ?? "").Trim(), out type);
    }
}
