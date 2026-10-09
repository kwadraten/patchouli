using FluentAssertions;
using Microsoft.FSharp.Core;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Tests;

/// <summary>
///     The 「工作流」settings section (ADR 0036, plan §3.7): the list shows the built-ins first, locked
///     definitions refuse mutation, creation copies a built-in definition and its script into an
///     unlocked workflow, and the save-time compile check blocks a broken script while accepting a
///     good one.
/// </summary>
/// <remarks>
///     Every test uses a real <see cref="WorkflowStore" /> over a throwaway directory and the real
///     F# compile check (F# Interactive), so the check is the same one the executor performs.
/// </remarks>
[Collection("Avalonia")]
public sealed class WorkflowSettingsViewModelTests : IDisposable
{
    private const string GoodScript = WorkflowScriptExecutorTests.AgentScript;

    private const string BrokenScript = """
                                        open Patchouli.Workflows.Scripting
                                        let broken = missingIdentifier + 1
                                        let run = Workflow.identity<WorkflowInput> |> Workflow.define
                                        """;

    private readonly string _root;
    private readonly WorkflowStore _store;
    private readonly MainWindowViewModel _main = new();
    private readonly WorkflowSettingsViewModel _section;

    public WorkflowSettingsViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "patchouli-workflow-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = WorkflowStore.ForLibrary(Path.Combine(_root, "library.db"));
        _section = new WorkflowSettingsViewModel(_main, Path.Combine(_root, "library.db"), _store);
    }

    [Fact]
    public async Task The_list_holds_the_built_ins_first_and_they_are_locked()
    {
        await _section.LoadAsync();

        _section.Workflows.Should().NotBeEmpty();
        _section.Workflows[0].IsBuiltIn.Should().BeTrue("built-ins are registered in code and listed first");
        _section.Workflows[0].Id.Should().Be(WorkflowIds.FullTextTranslation);
        _section.Workflows[0].ShowBuiltInBadge.Should().BeTrue();
        _section.Workflows[0].ShowLockedBadge.Should().BeTrue();
        _section.Workflows[0].IsLocked.Should().BeTrue();
        _section.Workflows[0].LockReason.Should().Contain("内置");
        _section.Workflows[0].MenuPlacementText.Should().Contain("Tools/Workflows");

        // The built-in is not stored on disk: the code registration is the single authority.
        File.Exists(_store.ResolveDefinitionPath(WorkflowIds.FullTextTranslation)).Should().BeFalse();
    }

    [Fact]
    public async Task The_editor_loads_a_built_in_as_read_only_with_its_embedded_script()
    {
        await _section.LoadAsync();

        WorkflowDefinitionItemViewModel builtIn = _section.Workflows[0];
        await _section.Editor.LoadAsync(builtIn);

        _section.Editor.CanEdit.Should().BeFalse();
        _section.Editor.IsReadOnly.Should().BeTrue();
        _section.Editor.LockExplanation.Should().Contain("内置工作流");
        _section.Editor.ScriptText.Should().Contain("let run : AgentWorkflow");
        _section.Editor.ParametersText.Should().Contain("documentId");
        _section.Editor.TrustWarning.Should().Be(WorkflowSettingsViewModel.TrustWarning);
        _section.Editor.TrustWarning.Should().Contain("宿主权限");

        // Saving a built-in is refused before anything is compiled or written.
        _section.Editor.Name = "renamed built-in";
        (await _section.Editor.SaveAsync()).Should().BeFalse();
        _section.Editor.ResultIsError.Should().BeTrue();
    }

    [Fact]
    public async Task A_locked_definition_is_refused_by_the_store_and_by_the_section()
    {
        await _section.LoadAsync();
        WorkflowDefinition builtIn = _section.Workflows[0].Source;

        // The store itself is the authority: a built-in id can never be written or deleted.
        WorkflowMutationResult saved = await _store.SaveAsync(builtIn, CancellationToken.None);
        WorkflowMutations.KindOf(saved).Should().Be(WorkflowMutationKind.Locked);
        WorkflowMutationResult deleted = await _store.DeleteAsync(builtIn.Id, CancellationToken.None);
        WorkflowMutations.KindOf(deleted).Should().Be(WorkflowMutationKind.Locked);

        // The section reports the same refusal and keeps the built-in in the list.
        _section.SelectedWorkflow = _section.Workflows[0];
        _section.CanDeleteSelected.Should().BeFalse();
        _section.DeleteExplanation.Should().Contain("内置工作流");
        (await _section.DeleteAsync()).Should().BeFalse();
        File.Exists(_store.ResolveDefinitionPath(builtIn.Id)).Should().BeFalse();
        _section.Workflows.Should().Contain(item => item.Id == builtIn.Id);
    }

    [Fact]
    public async Task Copying_a_built_in_produces_an_unlocked_copy_with_the_same_script()
    {
        await _section.LoadAsync();

        (await _section.CopyAsync()).Should().BeTrue($"status: {_section.Status}");

        WorkflowDefinitionItemViewModel copy = _section.Workflows[1];
        copy.IsBuiltIn.Should().BeFalse();
        copy.IsLocked.Should().BeFalse();
        copy.Id.Should().NotBe(WorkflowIds.FullTextTranslation);
        copy.Source.ScriptEntryPoint.Should().Be(
            BuiltInWorkflows.fullTextTranslation.ScriptEntryPoint);
        copy.Source.Parameters.Select(parameter => parameter.Name).Should()
            .Equal(BuiltInWorkflows.fullTextTranslation.Parameters.Select(parameter => parameter.Name));
        copy.Source.SelectionScope.Should().Be(BuiltInWorkflows.fullTextTranslation.SelectionScope);

        // The copy is stored on disk with the built-in script body.
        File.Exists(_store.ResolveDefinitionPath(copy.Id)).Should().BeTrue();
        string copiedScript = File.ReadAllText(_store.ResolveScriptPath(copy.Id));
        copiedScript.Should().Be(BuiltInWorkflowScripts.tryGetText(WorkflowIds.FullTextTranslation)!.Value);

        // The copy is the selected, editable definition.
        _section.SelectedWorkflow.Should().BeSameAs(copy);
        _section.Editor.CanEdit.Should().BeTrue();
        _section.Editor.LockExplanation.Should().BeEmpty();
    }

    [Fact]
    public async Task A_broken_script_is_blocked_by_the_compile_check_and_nothing_is_written()
    {
        await _section.LoadAsync();
        await _section.CopyAsync();
        WorkflowDefinitionItemViewModel copy = _section.Workflows[1];
        byte[] scriptBefore = File.ReadAllBytes(_store.ResolveScriptPath(copy.Id));

        _section.Editor.Name = "被改名的副本";
        _section.Editor.ScriptText = BrokenScript;
        (await _section.Editor.SaveAsync()).Should().BeFalse();

        _section.Editor.ResultIsError.Should().BeTrue();
        _section.Editor.ResultText.Should().Contain("编译检查");
        _section.Editor.HasDiagnostics.Should().BeTrue();
        _section.Editor.Diagnostics.Should().Contain(diagnostic => diagnostic.IsError);
        WorkflowScriptDiagnosticViewModel error =
            _section.Editor.Diagnostics.First(diagnostic => diagnostic.IsError);
        error.Line.Should().Be(2);
        error.Column.Should().BeGreaterThan(0);
        error.ErrorNumber.Should().Be("FS0039");
        error.Message.Should().Contain("missingIdentifier");
        error.DisplayText.Should().Contain("第 2 行");

        // The compile error blocked both writes: definition and script are unchanged.
        FSharpOption<WorkflowDefinition>? reloaded = await _store.TryLoadAsync(copy.Id, CancellationToken.None);
        reloaded!.Value.Name.Should().NotBe("被改名的副本");
        File.ReadAllBytes(_store.ResolveScriptPath(copy.Id)).Should().Equal(scriptBefore);
    }

    [Fact]
    public async Task Changing_the_entry_name_checks_and_saves_the_current_root_contract()
    {
        await _section.LoadAsync();
        await _section.CopyAsync();
        _section.Editor.ScriptEntryPoint = "execute";
        _section.Editor.ScriptText = GoodScript.Replace("let run :", "let execute :");
        (await _section.Editor.SaveAsync()).Should().BeTrue(_section.Editor.ResultText);
        WorkflowDefinition saved = (await _store.TryLoadAsync(_section.SelectedWorkflow!.Id, CancellationToken.None))!
            .Value;
        saved.ScriptEntryPoint.Should().Be("execute");
    }

    [Fact]
    public async Task A_good_script_passes_the_compile_check_and_is_saved_with_the_definition()
    {
        await _section.LoadAsync();
        await _section.CopyAsync();
        WorkflowDefinitionItemViewModel copy = _section.Workflows[1];

        _section.Editor.Name = "示例工作流";
        _section.Editor.Description = "按序翻译选中页";
        _section.Editor.MenuPath = "Tools/Workflows/User";
        _section.Editor.MenuOrder = 42;
        _section.Editor.ShowInMenu = false;
        _section.Editor.ScopeDocuments = true;
        _section.Editor.ScopePages = true;
        _section.Editor.ScopeSelection = false;
        _section.Editor.ParametersText = "documentId: DocumentId | required | 目标文档 | 默认=";
        _section.Editor.ScriptText = GoodScript;

        (await _section.Editor.SaveAsync()).Should().BeTrue($"{_section.Editor.ResultText} {_section.Status}");

        _section.Editor.ResultIsError.Should().BeFalse();
        _section.Editor.IsDirty.Should().BeFalse();
        _section.Editor.LastCheckSucceeded.Should().BeTrue();
        _section.Editor.Diagnostics.Should().NotContain(diagnostic => diagnostic.IsError);

        FSharpOption<WorkflowDefinition>? reloaded = await _store.TryLoadAsync(copy.Id, CancellationToken.None);
        WorkflowDefinition saved = reloaded!.Value;
        saved.Name.Should().Be("示例工作流");
        saved.Description.Should().Be("按序翻译选中页");
        saved.Menu.MenuPath.Should().Be("Tools/Workflows/User");
        saved.Menu.Order.Should().Be(42);
        saved.Menu.ShowInMenu.Should().BeFalse();
        saved.SelectionScope.Should().Be(WorkflowSelectionScope.DocumentsAndPages);
        saved.Parameters.Should().ContainSingle()
            .Which.Name.Should().Be("documentId");
        saved.Parameters[0].Required.Should().BeTrue();
        saved.Parameters[0].Type.Should().Be(WorkflowParameterType.DocumentId);
        saved.Locked.Should().BeFalse();
        saved.BuiltIn.Should().BeFalse();

        string written = File.ReadAllText(_store.ResolveScriptPath(copy.Id));
        string.Equals(written.Trim(), GoodScript.Trim(), StringComparison.Ordinal).Should()
            .BeTrue("the compiled script text is what the store holds");

        // A stale draft cannot modify a definition the user locked afterwards.
        WorkflowDefinition locked = new(saved.Id, saved.Name, saved.Description, saved.ScriptEntryPoint,
            saved.Parameters, saved.SelectionScope, true, saved.Menu, saved.BuiltIn);
        (await _store.SaveAsync(locked, CancellationToken.None)).Should().Be(WorkflowMutationResult.Applied);
        WorkflowMutationResult refused = await _store.SaveAsync(
            new WorkflowDefinition(saved.Id, "再次改名", saved.Description, saved.ScriptEntryPoint,
                saved.Parameters, saved.SelectionScope, false, saved.Menu, saved.BuiltIn), CancellationToken.None);
        WorkflowMutations.KindOf(refused).Should().Be(WorkflowMutationKind.Locked);
    }

    [Fact]
    public async Task New_workflow_compiles_and_runs_a_single_reply_without_tools()
    {
        await _section.LoadAsync();
        (await _section.CreateNewAsync()).Should().BeTrue(_section.Status);
        string script = _section.Editor.ScriptText;
        script.Should().NotBeEmpty();
        File.ReadAllText(_store.ResolveScriptPath(_section.SelectedWorkflow!.Id)).Should().Be(script);
        (await _section.Editor.CheckScriptAsync()).Should().BeTrue(_section.Editor.ResultText);

        HarnessStubHost host = new("你好！");
        using ScriptHostSession scriptHost = ScriptHostSession.Create();
        ScriptEvaluationResult evaluated = scriptHost.EvaluateScript(script, "new-workflow.fsx");
        evaluated.Succeeded.Should().BeTrue();
        AgentWorkflow plan = scriptHost.EvaluateExpression("run", "new-workflow.fsx").Value.Should()
            .BeOfType<AgentWorkflow>().Subject;
        WorkflowBounds bounds = Workflow.bounds(plan.Shape);
        bounds.ModelTurns.Should().Be(1);
        bounds.ToolCalls.Should().Be(0);
        bounds.EventWaits.Should().Be(0);
        WorkflowRunOutcome outcome = await new WorkflowExecutor(host).RunAsync(
            WorkflowScriptExecutorTests.Request(script), CancellationToken.None);
        outcome.Status.Should().Be(WorkflowRunStatus.Finished, outcome.Detail);
        host.Effects.Should().ContainSingle();
        host.Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task Copy_duplicates_the_selected_user_workflow_and_requires_a_selection()
    {
        _section.CopyCommand.CanExecute(null).Should().BeFalse();
        (await _section.CopyAsync()).Should().BeFalse();
        await _section.LoadAsync();
        await _section.CreateNewAsync();
        _section.Editor.Name = "我的回复";
        _section.Editor.Description = "自定义说明";
        _section.Editor.ScriptText = GoodScript;
        (await _section.Editor.SaveAsync()).Should().BeTrue(_section.Editor.ResultText);
        WorkflowDefinition source = _section.SelectedWorkflow!.Source;

        (await _section.CopyAsync()).Should().BeTrue(_section.Status);
        _section.SelectedWorkflow!.Id.Should().NotBe(source.Id);
        _section.SelectedWorkflow.Name.Should().Be("我的回复 副本");
        _section.SelectedWorkflow.Description.Should().Be(source.Description);
        _section.SelectedWorkflow.IsLocked.Should().BeFalse();
        _section.Editor.ScriptText.Should().Be(GoodScript);
    }

    [Fact]
    public async Task Deleting_works_only_for_an_unlocked_stored_workflow()
    {
        await _section.LoadAsync();
        await _section.CreateNewAsync();
        WorkflowDefinitionItemViewModel created = _section.Workflows.Last();
        created.IsBuiltIn.Should().BeFalse();
        _section.SelectedWorkflow.Should().BeSameAs(created);

        _section.CanDeleteSelected.Should().BeTrue();
        _section.DeleteExplanation.Should().BeEmpty();
        (await _section.DeleteAsync()).Should().BeTrue($"status: {_section.Status}");

        File.Exists(_store.ResolveDefinitionPath(created.Id)).Should().BeFalse();
        _section.Workflows.Should().NotContain(item => item.Id == created.Id);
        _section.Workflows.Should().OnlyContain(item => item.IsBuiltIn);

        // The built-ins stay: deletion is refused for them even when they are selected.
        _section.SelectedWorkflow = _section.Workflows[0];
        _section.CanDeleteSelected.Should().BeFalse();
        (await _section.DeleteAsync()).Should().BeFalse();
        _section.Workflows[0].Id.Should().Be(WorkflowIds.FullTextTranslation);
    }

    public void Dispose()
    {
        _main.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail the test run.
        }
    }
}
