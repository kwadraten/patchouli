using FluentAssertions;
using Patchouli.Host.Workflows;
using Patchouli.Workflows;

namespace Patchouli.Tests;

public sealed class WorkflowLegacyMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "workflow-declaration-migration-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Migration_preserves_defaults_and_backups_without_executing_the_script()
    {
        WorkflowStore store = new(_root);
        string marker = Path.Combine(_root, "must-not-be-created.txt");
        string original = """
                          open Patchouli.Workflows.Scripting
                          do System.IO.File.WriteAllText(MARKER, "executed")
                          let run : AgentWorkflow = Workflow.map (fun (_: WorkflowInput) -> "done") |> Workflow.define
                          """.Replace("MARKER", System.Text.Json.JsonSerializer.Serialize(marker),
            StringComparison.Ordinal);
        WorkflowDefinition definition = new("user.legacy", "旧工作流", "旧说明", "run",
            [new WorkflowParameter("limit", WorkflowParameterType.Integer, false, "最多次数", "3")],
            WorkflowSelectionScope.Nothing, false, new WorkflowMenuPlacement("Tools/Workflows", 20, true), false);
        await store.SaveAsync(definition, CancellationToken.None);
        await store.SaveScriptAsync(definition.Id, original, CancellationToken.None);

        ScriptCompiler compiler = new();
        WorkflowLegacyDeclarationMigrator migrator = new(store, compiler);
        await migrator.MigrateAsync();
        string migrated = (await store.ReadScriptAsync(definition.Id, CancellationToken.None))!.Value;
        WorkflowDeclarationAnalysis analysis =
            await compiler.AnalyzeWorkflowAsync(migrated, store.ResolveScriptPath(definition.Id), "run");

        analysis.Succeeded.Should().BeTrue(ScriptDiagnostics.describeAll(analysis.Diagnostics));
        analysis.Fields.Single(field => field.Key == "limit").DefaultValue.Should().Be("3");
        analysis.SessionModelKey.Should().Be("executionModel");
        File.Exists(marker).Should().BeFalse();
        File.ReadAllText(store.ResolveLegacyBackupPath(definition.Id) + ".fsx").Should().Be(original);
        await migrator.MigrateAsync();
        (await store.ReadScriptAsync(definition.Id, CancellationToken.None))!.Value.Should().Be(migrated);
    }

    [Fact]
    public async Task Unsupported_legacy_api_is_not_rewritten()
    {
        WorkflowStore store = new(_root);
        WorkflowDefinition definition = WorkflowDefinitions.create("user.unsupported", "Unsupported", "", "run");
        const string original = "let run : RemovedLegacyApi = failwith \"author repair required\"";
        await store.SaveAsync(definition, CancellationToken.None);
        await store.SaveScriptAsync(definition.Id, original, CancellationToken.None);

        await new WorkflowLegacyDeclarationMigrator(store, new ScriptCompiler()).MigrateAsync();

        (await store.ReadScriptAsync(definition.Id, CancellationToken.None))!.Value.Should().Be(original);
    }

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
    }
}
