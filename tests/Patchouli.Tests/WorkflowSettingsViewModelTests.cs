using Avalonia.Headless;
using FluentAssertions;
using Microsoft.FSharp.Core;
using Patchouli.Host.Workflows;
using Patchouli.UI;
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
public sealed class WorkflowSettingsViewModelTests
{
    private const string GoodScript = """
                                      open Patchouli.Workflows.Scripting
                                      open Patchouli.Workflows

                                      let info : WorkflowInfo =
                                          WorkflowInfo.create "示例工作流" "按序翻译选中页"
                                          |> WorkflowInfo.selectionScope WorkflowSelectionScope.DocumentsAndPages
                                          |> WorkflowInfo.menu "Tools/Workflows/User" 42

                                      let windowRadius =
                                          Parameter.integer "windowRadius" "窗口半径" 3
                                          |> Parameter.intRange 0 5

                                      let model = Parameter.model "model" "执行模型"

                                      let reply =
                                          Agent.text "reply" "只作简单回复。"
                                              (fun (_: WorkflowInput) -> "你好！")
                                          |> Agent.withTools []
                                          |> Agent.withBudget (AgentBudget.create 1 0)

                                      let run : AgentWorkflow =
                                          workflow { step reply }
                                          |> Workflow.define
                                          |> Workflow.withModel model
                                      """;

    private static readonly string BrokenScript = GoodScript.Replace(
        "let reply =", "let broken = missingIdentifier + 1\n\nlet reply =", StringComparison.Ordinal);

    private string _root = "";
    private WorkflowStore _store = null!;
    private MainWindowViewModel _main = null!;
    private WorkflowSettingsViewModel _section = null!;

    private async Task RunOnDispatcherAsync(Func<Task> test)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            _root = Path.Combine(Path.GetTempPath(), "patchouli-workflow-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = WorkflowStore.ForLibrary(Path.Combine(_root, "library.db"));
            _main = new MainWindowViewModel();
            _section = new WorkflowSettingsViewModel(_main, Path.Combine(_root, "library.db"), _store);
            try
            {
                await test();
            }
            finally
            {
                await _main.DisposeAsync();
                _main = null!;
                try
                {
                    Directory.Delete(_root, true);
                }
                catch (IOException)
                {
                    // A leftover temp directory must not fail the test run.
                }
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task The_list_holds_the_built_ins_first_and_they_are_locked()
    {
        return RunOnDispatcherAsync(The_list_holds_the_built_ins_first_and_they_are_locked_body);
    }

    private async Task The_list_holds_the_built_ins_first_and_they_are_locked_body()
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
    public Task The_editor_loads_a_built_in_as_read_only_with_its_embedded_script()
    {
        return RunOnDispatcherAsync(The_editor_loads_a_built_in_as_read_only_with_its_embedded_script_body);
    }

    private async Task The_editor_loads_a_built_in_as_read_only_with_its_embedded_script_body()
    {
        await _section.LoadAsync();

        WorkflowDefinitionItemViewModel builtIn = _section.Workflows[0];
        await _section.Editor.LoadAsync(builtIn);

        _section.Editor.CanEdit.Should().BeFalse();
        _section.Editor.IsReadOnly.Should().BeTrue();
        _section.Editor.LockExplanation.Should().Contain("内置工作流");
        _section.Editor.ScriptText.Should().Contain("let run : AgentWorkflow");
        _section.Editor.ParameterFields.Should().NotBeEmpty();
        _section.Editor.ParameterFields.Should().Contain(field => field.IsEnabled,
            "the built-in script stays locked while its saved parameter values remain configurable");
        _section.Editor.TrustWarning.Should().Be(WorkflowSettingsViewModel.TrustWarning);
        _section.Editor.TrustWarning.Should().Contain("宿主权限");

        string scriptBefore = _section.Editor.ScriptText;
        WorkflowParameterFieldViewModel targetLanguage =
            _section.Editor.ParameterFields.Single(field => field.Key == "targetLanguage");
        targetLanguage.Value = "fr";
        _section.Editor.ParameterFields.Single(field => field.Key == "model").Value =
            ModelSelectionCodec.Encode(new ModelSelection("openai", "test-model"));
        _section.Editor.CanSave.Should().BeTrue("configuration changes are allowed for a locked built-in");
        (await _section.Editor.SaveAsync()).Should().BeTrue(_section.Editor.ResultText);
        _section.Editor.ScriptText.Should().Be(scriptBefore);
        File.Exists(_store.ResolveScriptPath(builtIn.Id)).Should().BeFalse(
            "saving configuration must not write or replace the built-in script source");
        WorkflowConfigurationSnapshot savedConfiguration = await _section.Configuration.ReadAsync(builtIn.Id);
        savedConfiguration.Values["targetLanguage"].Should().Be("fr");

        // A built-in cannot be saved when only script-level changes would be required.
        (await _section.Editor.SaveAsync()).Should().BeFalse();
        _section.Editor.ResultIsError.Should().BeTrue();
    }

    [Fact]
    public Task A_locked_definition_is_refused_by_the_store_and_by_the_section()
    {
        return RunOnDispatcherAsync(A_locked_definition_is_refused_by_the_store_and_by_the_section_body);
    }

    private async Task A_locked_definition_is_refused_by_the_store_and_by_the_section_body()
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
    public Task Copying_a_built_in_produces_an_unlocked_copy_with_its_declared_name_updated()
    {
        return RunOnDispatcherAsync(Copying_a_built_in_produces_an_unlocked_copy_with_its_declared_name_updated_body);
    }

    private async Task Copying_a_built_in_produces_an_unlocked_copy_with_its_declared_name_updated_body()
    {
        await _section.LoadAsync();

        WorkflowConfigurationSnapshot sourceConfiguration =
            await _section.Configuration.ReadAsync(WorkflowIds.FullTextTranslation);

        (await _section.CopyAsync()).Should().BeTrue($"status: {_section.Status}");

        WorkflowDefinitionItemViewModel copy = _section.Workflows[1];
        copy.IsBuiltIn.Should().BeFalse();
        copy.IsLocked.Should().BeFalse();
        copy.Id.Should().NotBe(WorkflowIds.FullTextTranslation);
        copy.Source.ScriptEntryPoint.Should().Be(
            BuiltInWorkflows.fullTextTranslation.ScriptEntryPoint);
        copy.Source.Parameters.Select(parameter => parameter.Name).Should()
            .Equal(sourceConfiguration.Analysis.Fields.Select(field => field.Key));
        copy.Source.SelectionScope.Should().Be(BuiltInWorkflows.fullTextTranslation.SelectionScope);

        // The copy's name remains authoritative in its source declaration.
        File.Exists(_store.ResolveDefinitionPath(copy.Id)).Should().BeTrue();
        WorkflowConfigurationSnapshot copiedConfiguration = await _section.Configuration.ReadAsync(copy.Id);
        copiedConfiguration.Analysis.Info.Name.Should().Be("全文翻译 副本");
        copy.Name.Should().Be("全文翻译 副本");
        copiedConfiguration.Values.Should().Equal(sourceConfiguration.Values);

        // The copy is the selected, editable definition.
        _section.SelectedWorkflow.Should().BeSameAs(copy);
        _section.Editor.CanEdit.Should().BeTrue();
        _section.Editor.LockExplanation.Should().BeEmpty();
    }

    [Fact]
    public Task A_broken_script_is_blocked_by_the_compile_check_and_nothing_is_written()
    {
        return RunOnDispatcherAsync(A_broken_script_is_blocked_by_the_compile_check_and_nothing_is_written_body);
    }

    private async Task A_broken_script_is_blocked_by_the_compile_check_and_nothing_is_written_body()
    {
        await _section.LoadAsync();
        await _section.CopyAsync();
        WorkflowDefinitionItemViewModel copy = _section.Workflows[1];
        byte[] scriptBefore = File.ReadAllBytes(_store.ResolveScriptPath(copy.Id));

        _section.Editor.ScriptText = BrokenScript;
        (await _section.Editor.SaveAsync()).Should().BeFalse();

        _section.Editor.ResultIsError.Should().BeTrue();
        _section.Editor.ResultText.Should().Contain("编译检查");
        _section.Editor.HasDiagnostics.Should().BeTrue();
        _section.Editor.Diagnostics.Should().Contain(diagnostic => diagnostic.IsError);
        WorkflowScriptDiagnosticViewModel error =
            _section.Editor.Diagnostics.First(diagnostic => diagnostic.IsError);
        error.Line.Should().BeGreaterThan(0);
        error.Column.Should().BeGreaterThan(0);
        error.ErrorNumber.Should().Be("FS0039");
        error.Message.Should().Contain("missingIdentifier");
        error.DisplayText.Should().Contain($"第 {error.Line} 行");

        // The compile error blocked both writes: definition and script are unchanged.
        FSharpOption<WorkflowDefinition>? reloaded = await _store.TryLoadAsync(copy.Id, CancellationToken.None);
        File.ReadAllBytes(_store.ResolveScriptPath(copy.Id)).Should().Equal(scriptBefore);
    }

    [Fact]
    public Task Changing_the_entry_name_checks_and_saves_the_current_root_contract()
    {
        return RunOnDispatcherAsync(Changing_the_entry_name_checks_and_saves_the_current_root_contract_body);
    }

    private async Task Changing_the_entry_name_checks_and_saves_the_current_root_contract_body()
    {
        await _section.LoadAsync();
        await _section.CopyAsync();
        SetModelValue();
        _section.Editor.ScriptText = GoodScript
            .Replace("|> WorkflowInfo.selectionScope",
                "|> WorkflowInfo.entryPoint \"execute\"\n                                           |> WorkflowInfo.selectionScope",
                StringComparison.Ordinal)
            .Replace("let run :", "let execute :", StringComparison.Ordinal);
        (await _section.Editor.SaveAsync()).Should().BeTrue(_section.Editor.ResultText);
        WorkflowDefinition saved = (await _store.TryLoadAsync(_section.SelectedWorkflow!.Id, CancellationToken.None))!
            .Value;
        saved.ScriptEntryPoint.Should().Be("execute");
    }

    [Fact]
    public Task A_good_script_passes_the_compile_check_and_is_saved_with_the_definition()
    {
        return RunOnDispatcherAsync(A_good_script_passes_the_compile_check_and_is_saved_with_the_definition_body);
    }

    private async Task A_good_script_passes_the_compile_check_and_is_saved_with_the_definition_body()
    {
        await _section.LoadAsync();
        await _section.CopyAsync();
        WorkflowDefinitionItemViewModel copy = _section.Workflows[1];

        SetModelValue();
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
        saved.Menu.ShowInMenu.Should().BeTrue();
        saved.SelectionScope.Should().Be(WorkflowSelectionScope.DocumentsAndPages);
        saved.Parameters.Should().Contain(parameter => parameter.Name == "windowRadius");
        saved.Parameters.Should().Contain(parameter => parameter.Name == "model" && parameter.Required);
        saved.Parameters.Single(parameter => parameter.Name == "windowRadius").Type
            .Should().Be(WorkflowParameterType.Integer);
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
    public Task New_workflow_compiles_and_runs_a_single_reply_without_tools()
    {
        return RunOnDispatcherAsync(New_workflow_compiles_and_runs_a_single_reply_without_tools_body);
    }

    private async Task New_workflow_compiles_and_runs_a_single_reply_without_tools_body()
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
    public Task Copy_duplicates_the_selected_user_workflow_and_requires_a_selection()
    {
        return RunOnDispatcherAsync(Copy_duplicates_the_selected_user_workflow_and_requires_a_selection_body);
    }

    private async Task Copy_duplicates_the_selected_user_workflow_and_requires_a_selection_body()
    {
        _section.CopyCommand.CanExecute(null).Should().BeFalse();
        (await _section.CopyAsync()).Should().BeFalse();
        await _section.LoadAsync();
        await _section.CreateNewAsync();
        _section.Editor.ScriptText = GoodScript;
        SetModelValue();
        (await _section.Editor.SaveAsync()).Should().BeTrue(_section.Editor.ResultText);
        WorkflowDefinition source = _section.SelectedWorkflow!.Source;

        (await _section.CopyAsync()).Should().BeTrue(_section.Status);
        _section.SelectedWorkflow!.Id.Should().NotBe(source.Id);
        _section.SelectedWorkflow.Name.Should().Be("示例工作流 副本");
        _section.SelectedWorkflow.Description.Should().Be(source.Description);
        _section.SelectedWorkflow.IsLocked.Should().BeFalse();
        WorkflowConfigurationSnapshot copiedConfiguration =
            await _section.Configuration.ReadAsync(_section.SelectedWorkflow.Id);
        copiedConfiguration.Analysis.Info.Name.Should().Be("示例工作流 副本");
    }

    [Fact]
    public Task Deleting_works_only_for_an_unlocked_stored_workflow()
    {
        return RunOnDispatcherAsync(Deleting_works_only_for_an_unlocked_stored_workflow_body);
    }

    private async Task Deleting_works_only_for_an_unlocked_stored_workflow_body()
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

    private void SetModelValue()
    {
        _section.Editor.ParameterFields.Single(field => field.Key == "model").Value =
            ModelSelectionCodec.Encode(new ModelSelection("openai", "test-model"));
    }
}
