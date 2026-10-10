using FluentAssertions;
using Patchouli.Host.Workflows;
using Patchouli.Llm;
using Patchouli.Workflows;

namespace Patchouli.Tests;

public sealed class WorkflowConfigurationTests
{
    private const string WorkflowId = "user.configuration-test";

    [Fact]
    public async Task Values_round_trip_and_stale_declaration_fingerprints_are_rejected()
    {
        await using Harness harness = await Harness.CreateAsync(Script("prompt"));
        WorkflowConfigurationSnapshot initial = await harness.Configuration.ReadAsync(WorkflowId);
        initial.Analysis.Succeeded.Should().BeTrue();

        WorkflowConfigurationSaveResult saved = await harness.Configuration.SaveAsync(WorkflowId,
            initial.DeclarationFingerprint,
            new Dictionary<string, string>
            {
                ["model"] = "{\"providerId\":\"provider-a\",\"model\":\"model-a\"}",
                ["prompt"] = "Explain this text."
            });

        saved.Saved.Should().BeTrue();
        WorkflowConfigurationSnapshot reloaded = await harness.Configuration.ReadAsync(WorkflowId);
        reloaded.Values.Should().ContainKey("model").WhoseValue.Should()
            .Be("{\"providerId\":\"provider-a\",\"model\":\"model-a\"}");
        reloaded.Values.Should().ContainKey("prompt").WhoseValue.Should().Be("Explain this text.");

        WorkflowConfigurationSaveResult stale = await harness.Configuration.SaveAsync(WorkflowId,
            "old-declaration-fingerprint", new Dictionary<string, string> { ["prompt"] = "stale" });
        stale.Saved.Should().BeFalse();
        stale.Issues.Should().ContainSingle(issue => issue.Code == "declaration_changed");
    }

    [Fact]
    public async Task Draft_analysis_is_read_only_and_context_bound_required_values_are_not_saved()
    {
        await using Harness harness = await Harness.CreateAsync(Script("prompt"));
        string definitionPath = harness.Store.ResolveDefinitionPath(WorkflowId);
        string definitionBefore = await File.ReadAllTextAsync(definitionPath);
        WorkflowConfigurationSnapshot original = await harness.Configuration.ReadAsync(WorkflowId);

        WorkflowConfigurationSnapshot draft =
            await harness.Configuration.ReadAsync(WorkflowId, Script("draftPrompt"), "run");
        draft.Analysis.Succeeded.Should().BeTrue();
        draft.DeclarationFingerprint.Should().NotBe(original.DeclarationFingerprint);
        (await File.ReadAllTextAsync(definitionPath)).Should().Be(definitionBefore,
            "an unsaved editor draft must not update the generated workflow metadata");

        WorkflowConfigurationSaveResult saved = await harness.Configuration.SaveAsync(WorkflowId,
            original.DeclarationFingerprint,
            new Dictionary<string, string>
            {
                ["model"] = "{\"providerId\":\"provider-a\",\"model\":\"model-a\"}",
                ["prompt"] = "stored prompt"
            });
        saved.Saved.Should().BeTrue("the required document selection comes from launch context, not settings");
        (await harness.Configuration.ReadAsync(WorkflowId)).Values.Should().NotContainKey("sourceDocs");
    }

    [Fact]
    public async Task Launch_resolution_uses_declared_context_bindings_and_explicit_values_win()
    {
        await using Harness harness = await Harness.CreateAsync(Script("prompt"));
        WorkflowConfigurationSnapshot snapshot = await harness.Configuration.ReadAsync(WorkflowId);
        WorkflowConfigurationResolution resolution = harness.Configuration.ResolveLaunch(snapshot,
            new Dictionary<string, string>
            {
                ["sourceDocs"] = "[\"explicit-doc\"]",
                ["prompt"] = "explicit prompt",
                ["model"] = "{\"providerId\":\"provider-a\",\"model\":\"model-a\"}"
            },
            new Dictionary<string, string>
            {
                ["documents"] = "[\"context-doc\"]",
                ["pageRange"] = "2-4"
            });

        resolution.Succeeded.Should().BeTrue();
        resolution.Values["sourceDocs"].Should().Be("[\"explicit-doc\"]");
        resolution.Values["pageRangeField"].Should().Be("2-4");
        resolution.Values.Should().NotContainKey("pageRange");
        resolution.ModelSelection.Should().NotBeNull();
        resolution.ModelSelection!.ProviderId.Should().Be("provider-a");
        resolution.ModelSelection.Model.Should().Be("model-a");
    }

    [Fact]
    public async Task Launch_requires_a_model_bound_to_the_workflow()
    {
        string script = Script("prompt")
            .Replace("let model = Parameter.model \"model\" \"Model\"", string.Empty,
                StringComparison.Ordinal)
            .Replace("|> Workflow.withModel model", string.Empty, StringComparison.Ordinal);
        await using Harness harness = await Harness.CreateAsync(script);
        WorkflowConfigurationSnapshot snapshot = await harness.Configuration.ReadAsync(WorkflowId);

        WorkflowConfigurationResolution resolution = harness.Configuration.ResolveLaunch(snapshot,
            new Dictionary<string, string>
            {
                ["sourceDocs"] = "[\"doc\"]",
                ["prompt"] = "prompt"
            },
            new Dictionary<string, string>());

        resolution.Succeeded.Should().BeFalse();
        resolution.Issues.Should().ContainSingle(issue => issue.Code == "model_binding_missing");
    }

    private static string Script(string textKey)
    {
        return $$"""
                 open Patchouli.Workflows
                 open Patchouli.Workflows.Scripting
                 let info =
                     WorkflowInfo.create "Configuration test" "Tests static declarations."
                     |> WorkflowInfo.selectionScope WorkflowSelectionScope.All
                 let model = Parameter.model "model" "Model"
                 let sourceDocs =
                     Parameter.documents "sourceDocs" "Documents"
                     |> Parameter.required "Select at least one document."
                     |> Parameter.bindContext "documents"
                 let pageRange =
                     Parameter.pageRange "pageRangeField" "Page range" ""
                     |> Parameter.bindContext "pageRange"
                 let {{textKey}} =
                     Parameter.text "{{textKey}}" "Prompt" "default"
                     |> Parameter.required "A prompt is required."
                 let run : AgentWorkflow =
                     Workflow.identity<WorkflowInput>
                     |> Workflow.define
                     |> Workflow.withModel model
                 """;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root;

        private Harness(string root, WorkflowStore store)
        {
            _root = root;
            Store = store;
            Configuration = new WorkflowConfigurationService(store, LlmAppSettings.Default);
        }

        public WorkflowStore Store { get; }
        public WorkflowConfigurationService Configuration { get; }

        public static async Task<Harness> CreateAsync(string script)
        {
            string root = Path.Combine(Path.GetTempPath(), "patchouli-workflow-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            WorkflowStore store = new(root);
            WorkflowMutationResult definition = await store.SaveAsync(
                WorkflowDefinitions.create(WorkflowId, "Configuration test", "", "run"), CancellationToken.None);
            definition.IsApplied.Should().BeTrue();
            WorkflowMutationResult savedScript =
                await store.SaveScriptAsync(WorkflowId, script, CancellationToken.None);
            savedScript.IsApplied.Should().BeTrue();
            return new Harness(root, store);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
