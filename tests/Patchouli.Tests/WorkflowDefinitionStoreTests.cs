using FluentAssertions;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Tests;

/// <summary>
///     Verifies the S2 workflow definition model and its storage (ADR 0036, plan §3.2): the metadata a
///     workflow carries (id, name, description, script entry point, parameter definitions, selection
///     scope, locked state, menu placement), validation, built-ins that are registered in code and
///     cannot be edited or deleted, user configuration that round-trips through the Library's
///     <c>workflows/</c> directory, and the script snapshot a session keeps unchanged while it runs.
/// </summary>
/// <remarks>Every test works on a temporary directory: no real Library and no network access.</remarks>
public sealed class WorkflowDefinitionStoreTests
{
    private static readonly string[] NoProblems = [];

    private static string CreateTempDirectory()
    {
        return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"patchouli-workflows-{Guid.NewGuid():N}"))
            .FullName;
    }

    private static WorkflowDefinition Definition(string id, string name = "Translate",
        WorkflowParameter[]? parameters = null, bool locked = false, bool showInMenu = true,
        string entryPoint = "run")
    {
        return new WorkflowDefinition(
            id,
            name,
            "Translates the selected pages.",
            entryPoint,
            parameters ?? [],
            WorkflowSelectionScope.DocumentsAndPages,
            locked,
            new WorkflowMenuPlacement(WorkflowDefinitions.DefaultMenuPath, 20, showInMenu),
            false);
    }

    [Fact]
    public void Built_in_full_text_translation_is_locked_and_has_no_script_body_yet()
    {
        WorkflowDefinition builtIn = BuiltInWorkflows.fullTextTranslation;

        builtIn.Id.Should().Be(WorkflowIds.FullTextTranslation);
        builtIn.Locked.Should().BeTrue();
        builtIn.BuiltIn.Should().BeTrue();
        builtIn.Menu.ShowInMenu.Should().BeTrue();
        builtIn.Menu.Order.Should().BeLessThan(100);
        builtIn.Parameters.Select(parameter => parameter.Name)
            .Should().Equal("documentId", "pageRange", "targetLanguage");
        builtIn.Parameters.Single(parameter => parameter.Required).Name.Should().Be("documentId");
        builtIn.SelectionScope.Should().Be(WorkflowSelectionScope.DocumentsAndPages);

        BuiltInWorkflows.all.Should().ContainSingle();
        BuiltInWorkflows.isBuiltIn(WorkflowIds.FullTextTranslation).Should().BeTrue();
        BuiltInWorkflows.isBuiltIn("user.translate").Should().BeFalse();
        BuiltInWorkflows.tryFind(WorkflowIds.FullTextTranslation)!.Value.Id.Should()
            .Be(WorkflowIds.FullTextTranslation);

        // The script body is registered now: the built-in ships it as an embedded script, and it is
        // what the store hands out and what BuiltInTranslationWorkflowTests runs.
        BuiltInWorkflowScripts.hasText(WorkflowIds.FullTextTranslation).Should().BeTrue();
        BuiltInWorkflowScripts.tryGetText(WorkflowIds.FullTextTranslation)!.Value.Should()
            .Contain("let run : AgentWorkflow");
    }

    [Fact]
    public void Validation_reports_every_problem_of_a_definition()
    {
        WorkflowDefinition invalid = new(
            "Bad Id",
            " ",
            string.Empty,
            "not a value name",
            [
                new WorkflowParameter("pageRange", WorkflowParameterType.PageRange, false, string.Empty, string.Empty),
                new WorkflowParameter("pageRange", WorkflowParameterType.PageRange, false, string.Empty, string.Empty)
            ],
            WorkflowSelectionScope.Documents,
            false,
            new WorkflowMenuPlacement(string.Empty, 0, true),
            false);

        WorkflowValidation validation = WorkflowDefinitions.validate(invalid);

        validation.IsValid.Should().BeFalse();
        validation.Problems.Should().HaveCount(5);
        validation.Problems.Should().Contain(problem => problem.Contains("workflow id", StringComparison.Ordinal));
        validation.Problems.Should().Contain(problem => problem.Contains("name is required", StringComparison.Ordinal));
        validation.Problems.Should().Contain(problem => problem.Contains("entry point", StringComparison.Ordinal));
        validation.Problems.Should().Contain(problem => problem.Contains("menu path", StringComparison.Ordinal));

        WorkflowDefinitions.validate(Definition("user.translate", parameters:
        [
            new WorkflowParameter("documentId", WorkflowParameterType.DocumentId, true, "Document", string.Empty)
        ])).Problems.Should().Equal(NoProblems);

        WorkflowValidation missing = WorkflowDefinitions.validate(null!);
        missing.IsValid.Should().BeFalse();
        missing.Problems.Should().ContainSingle();
    }

    [Fact]
    public async Task Definition_round_trips_with_parameters_menu_and_selection_scope()
    {
        string root = CreateTempDirectory();
        WorkflowStore store = new(root);

        WorkflowDefinition definition = new(
            "user.page-translation",
            "Page translation",
            "Translates one page range.",
            "translate",
            [
                new WorkflowParameter("documentId", WorkflowParameterType.DocumentId, true, "Document", "doc-1"),
                new WorkflowParameter("targetLanguage", WorkflowParameterType.Language, false, "Language", "zh")
            ],
            WorkflowSelectionScope.Pages | WorkflowSelectionScope.Selection,
            false,
            new WorkflowMenuPlacement("Tools/Workflows/Translation", 5, true),
            false);

        (await store.SaveAsync(definition, CancellationToken.None)).IsApplied.Should().BeTrue();
        (await store.SaveScriptAsync(definition.Id, "let run : AgentWorkflow = ()",
                CancellationToken.None))
            .IsApplied.Should().BeTrue();

        WorkflowDefinition loaded = (await store.TryLoadAsync(definition.Id, CancellationToken.None))!.Value;
        loaded.Should().BeEquivalentTo(definition);
        (await store.ReadScriptAsync(definition.Id, CancellationToken.None))!.Value
            .Should().Be("let run : AgentWorkflow = ()");

        // The definition and its script live next to the Library, as user configuration.
        store.ResolveDefinitionPath(definition.Id).Should().StartWith(root);
        File.Exists(store.ResolveDefinitionPath(definition.Id)).Should().BeTrue();
        File.Exists(store.ResolveScriptPath(definition.Id)).Should().BeTrue();
        store.ResolveScriptPath(definition.Id).Should().EndWith("page-translation.fsx");

        WorkflowStore libraryStore = WorkflowStore.ForLibrary(Path.Combine(root, "library.sqlite"));
        libraryStore.Root.Should().Be(Path.Combine(root, "workflows"));
    }

    [Fact]
    public async Task Definitions_are_listed_built_ins_first_then_stored_workflows()
    {
        string root = CreateTempDirectory();
        WorkflowStore store = new(root);

        (await store.SaveAsync(Definition("user.zeta"), CancellationToken.None)).IsApplied.Should().BeTrue();
        (await store.SaveAsync(Definition("user.alpha"), CancellationToken.None)).IsApplied.Should().BeTrue();

        IReadOnlyList<WorkflowDefinition> definitions = await store.ListDefinitionsAsync(CancellationToken.None);

        definitions.Select(definition => definition.Id)
            .Should().Equal(WorkflowIds.FullTextTranslation, "user.alpha", "user.zeta");
        (await store.ListIdsAsync(CancellationToken.None)).Should().Equal("user.alpha", "user.zeta");

        // A missing workflows root is a normal condition, not an error: a Library always opens.
        WorkflowStore empty = new(Path.Combine(root, "missing"));
        (await empty.ListIdsAsync(CancellationToken.None)).Should().BeEmpty();
        (await empty.TryLoadAsync("user.alpha", CancellationToken.None)).Should().BeNull();
        (await empty.ReadScriptAsync("user.alpha", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Locked_built_in_cannot_be_saved_deleted_or_given_a_script()
    {
        string root = CreateTempDirectory();
        WorkflowStore store = new(root);
        WorkflowDefinition builtIn = BuiltInWorkflows.fullTextTranslation;

        WorkflowMutationResult saved = await store.SaveAsync(builtIn, CancellationToken.None);
        saved.IsLocked.Should().BeTrue();
        ((WorkflowMutationResult.Locked)saved).workflowId.Should().Be(WorkflowIds.FullTextTranslation);

        // Even a user definition that claims the built-in id is refused.
        (await store.SaveAsync(Definition(WorkflowIds.FullTextTranslation), CancellationToken.None))
            .IsLocked.Should().BeTrue();
        (await store.DeleteAsync(WorkflowIds.FullTextTranslation, CancellationToken.None)).IsLocked.Should().BeTrue();
        (await store.SaveScriptAsync(WorkflowIds.FullTextTranslation, "let run _ = ()", CancellationToken.None))
            .IsLocked.Should().BeTrue();

        File.Exists(Path.Combine(root, WorkflowIds.FullTextTranslation + ".json")).Should().BeFalse();

        // The registered built-in is what every caller sees, regardless of the store.
        (await store.TryLoadAsync(WorkflowIds.FullTextTranslation, CancellationToken.None))!.Value.Locked.Should()
            .BeTrue();
    }

    [Fact]
    public async Task A_locked_user_workflow_cannot_be_modified_or_deleted()
    {
        string root = CreateTempDirectory();
        WorkflowStore store = new(root);

        (await store.SaveAsync(Definition("user.pinned", locked: true), CancellationToken.None)).IsApplied.Should()
            .BeTrue();

        WorkflowMutationResult modified =
            await store.SaveAsync(Definition("user.pinned", "Renamed"), CancellationToken.None);
        modified.IsLocked.Should().BeTrue();
        ((WorkflowDefinition)(await store.TryLoadAsync("user.pinned", CancellationToken.None))!.Value).Name
            .Should().Be("Translate");

        (await store.SaveScriptAsync("user.pinned", "let run _ = ()", CancellationToken.None)).IsLocked.Should()
            .BeTrue();
        (await store.DeleteAsync("user.pinned", CancellationToken.None)).IsLocked.Should().BeTrue();
        (await store.TryLoadAsync("user.pinned", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Invalid_and_unknown_workflows_are_reported_without_writing()
    {
        string root = CreateTempDirectory();
        WorkflowStore store = new(root);

        WorkflowMutationResult invalid =
            await store.SaveAsync(Definition("INVALID ID"), CancellationToken.None);
        invalid.IsInvalid.Should().BeTrue();
        ((WorkflowMutationResult.Invalid)invalid).problems.Should().NotBeEmpty();
        Directory.EnumerateFiles(root, "*.json").Should().BeEmpty();
        (await store.ListIdsAsync(CancellationToken.None)).Should().BeEmpty();

        (await store.SaveScriptAsync("user.absent", "let run _ = ()", CancellationToken.None)).IsNotFound.Should()
            .BeTrue();
        (await store.DeleteAsync("user.absent", CancellationToken.None)).IsNotFound.Should().BeTrue();
        (await store.TryLoadAsync("user.absent", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_a_workflow_removes_its_definition_and_script()
    {
        string root = CreateTempDirectory();
        WorkflowStore store = new(root);
        WorkflowDefinition definition = Definition("user.temporary");

        (await store.SaveAsync(definition, CancellationToken.None)).IsApplied.Should().BeTrue();
        (await store.SaveScriptAsync(definition.Id, "let run _ = ()", CancellationToken.None)).IsApplied.Should()
            .BeTrue();

        (await store.DeleteAsync(definition.Id, CancellationToken.None)).IsApplied.Should().BeTrue();
        File.Exists(store.ResolveDefinitionPath(definition.Id)).Should().BeFalse();
        File.Exists(store.ResolveScriptPath(definition.Id)).Should().BeFalse();
        (await store.TryLoadAsync(definition.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public void Step_records_round_trip_through_the_durable_codec()
    {
        WorkflowStepRecord step = new(3, 7L, "LlmChat", "translate page 1", true, "translated");

        WorkflowCodec.stepFromJson(WorkflowCodec.stepToJson(step)).Should().BeEquivalentTo(step);
        WorkflowCodec.stepToJson(step).Should().NotContain("\n");
    }

    [Fact]
    public async Task A_session_keeps_the_snapshot_it_started_with()
    {
        string root = CreateTempDirectory();
        FileWorkflowRunSink sink = new(Path.Combine(root, "agent-sessions"));
        WorkflowDefinition definition = Definition("user.snapshot");
        DateTimeOffset capturedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        WorkflowScriptSnapshot first =
            WorkflowSnapshots.capture(definition, "let run _ = ()", ScriptApiVersion.Current, capturedAt);
        WorkflowScriptSnapshot second =
            WorkflowSnapshots.capture(definition, "let run _ = 1", ScriptApiVersion.Current, capturedAt);

        first.ApiVersion.Should().Be(ScriptApiVersion.Current);
        first.CapturedAt.Should().Be("2026-01-02T03:04:05.0000000+00:00");
        first.ScriptHash.Should().Be(WorkflowSnapshots.hashScript("let run _ = ()"));
        first.ScriptHash.Should().NotBe(second.ScriptHash);

        await sink.RecordScriptSnapshotAsync("session-1", first, CancellationToken.None);
        // Recording again (for example after the workflow definition changed) never rewrites the
        // snapshot of a session that already started.
        await sink.RecordScriptSnapshotAsync("session-1", second, CancellationToken.None);

        WorkflowScriptSnapshot stored =
            (await sink.ReadSnapshotAsync("session-1", CancellationToken.None))!.Value;
        stored.ScriptText.Should().Be("let run _ = ()");
        stored.ScriptHash.Should().Be(first.ScriptHash);

        string directory = sink.ResolveWorkflowDirectory("session-1");
        File.ReadAllText(Path.Combine(directory, "script.fsx")).Should().Be("let run _ = ()");

        // The session log keeps every performed step, in recorded order.
        await sink.RecordStepAsync("session-1", new WorkflowStepRecord(1, 0L, "LlmChat",
            "preface", true, "preface"), CancellationToken.None);
        await sink.RecordStepAsync("session-1", new WorkflowStepRecord(2, 4L, "McpToolCall",
            "patchouli://documents/doc-1", true, "source text"), CancellationToken.None);

        WorkflowStepRecord[] steps = await sink.ReadStepsAsync("session-1", CancellationToken.None);
        steps.Should().HaveCount(2);
        steps[1].EffectId.Should().Be(4L);
        steps[1].Result.Should().Be("source text");
        (await sink.ReadStepsAsync("session-absent", CancellationToken.None)).Should().BeEmpty();
        (await sink.ReadSnapshotAsync("session-absent", CancellationToken.None)).Should().BeNull();
    }
}
