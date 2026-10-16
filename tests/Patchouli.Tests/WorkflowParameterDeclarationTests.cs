using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;
using Xunit;

namespace Patchouli.Tests;

public sealed class WorkflowParameterDeclarationTests
{
    [Fact]
    public async Task Analyzer_uses_the_declared_entry_point_instead_of_the_cached_entry_point()
    {
        const string script = """
                              open Patchouli.Workflows
                              open Patchouli.Workflows.Scripting
                              let info = WorkflowInfo.create "Source authority" ""
                                         |> WorkflowInfo.entryPoint "execute"
                              let model = Parameter.model "model" "Model"
                              let run : AgentWorkflow = Workflow.identity<WorkflowInput> |> Workflow.define
                              let execute : AgentWorkflow =
                                  Workflow.identity<WorkflowInput>
                                  |> Workflow.define
                                  |> Workflow.withModel model
                              """;

        WorkflowDeclarationAnalysis analysis =
            await new ScriptCompiler().AnalyzeWorkflowAsync(script, "entry-authority.fsx", "run");

        analysis.Succeeded.Should().BeTrue(FormatDiagnostics(analysis));
        analysis.Info.EntryPoint.Should().Be("execute");
        analysis.Fields.Should().ContainSingle(field => field.Key == "model");
        analysis.SessionModelKey.Should().Be("model");
    }

    [Fact]
    public async Task Analyzer_ignores_comments_and_local_bindings_and_keeps_alias_and_model_identity()
    {
        const string script = """
                              open Patchouli.Workflows
                              open Patchouli.Workflows.Scripting
                              // let fake = Parameter.text "fake" "Fake" "ignored"
                              let info : WorkflowInfo =
                                  WorkflowInfo.create "Static declarations" "No author code is evaluated."
                                  |> WorkflowInfo.selectionScope WorkflowSelectionScope.All
                              let model = Parameter.model "runtime-model" "Model"
                              let modelAlias = model
                              let prompt =
                                  Parameter.text
                                      "stable-prompt-key"
                                      "Prompt"
                                      ""
                                  |> Parameter.required "A prompt is required."
                                  |> Parameter.describe "Text supplied to the model."
                              let promptAlias = prompt
                              let nested () =
                                  let local = Parameter.text "local-only" "Local" "hidden"
                                  local
                              let readPrompt (input: WorkflowInput) = Parameter.get promptAlias input
                              let run : AgentWorkflow =
                                  Workflow.identity<WorkflowInput>
                                  |> Workflow.define
                                  |> Workflow.withModel modelAlias
                              """;

        WorkflowDeclarationAnalysis analysis =
            await new ScriptCompiler().AnalyzeWorkflowAsync(script, "declaration-test.fsx", "run");

        analysis.Succeeded.Should().BeTrue(FormatDiagnostics(analysis));
        analysis.Fields.Select(field => field.Key).Should().Equal("runtime-model", "stable-prompt-key");
        analysis.Fields.Should().NotContain(field => field.Key == "fake" || field.Key == "local-only");
        ParameterDescriptor prompt = analysis.Fields.Single(field => field.Key == "stable-prompt-key");
        prompt.Required.Should().BeTrue();
        prompt.Description.Should().Be("Text supplied to the model.");
        analysis.SessionModelKey.Should().Be("runtime-model");
        analysis.Info.Name.Should().Be("Static declarations");
    }

    [Fact]
    public async Task Analyzer_rejects_computed_parameter_defaults_with_a_source_diagnostic()
    {
        const string script = """
                              open Patchouli.Workflows
                              open Patchouli.Workflows.Scripting
                              let info = WorkflowInfo.create "Computed" ""
                              let runtimeDefault = System.DateTime.UtcNow.ToString()
                              let prompt = Parameter.text "prompt" "Prompt" runtimeDefault
                              let run : AgentWorkflow = Workflow.identity<WorkflowInput> |> Workflow.define
                              """;

        WorkflowDeclarationAnalysis analysis =
            await new ScriptCompiler().AnalyzeWorkflowAsync(script, "computed-default.fsx", "run");

        analysis.Succeeded.Should().BeFalse();
        analysis.Diagnostics.Should().Contain(diagnostic => diagnostic.ErrorNumber == "WORKFLOW_DECLARATION_STATIC");
        analysis.Diagnostics.Should().Contain(diagnostic => diagnostic.Line == 5);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("description")]
    [InlineData("range")]
    [InlineData("context")]
    public async Task Analyzer_rejects_dynamic_parameter_modifiers(string modifierKind)
    {
        string modifier = modifierKind switch
        {
            "required" => "Parameter.required requiredMessage (Parameter.text \"value\" \"Value\" \"\")",
            "description" => "Parameter.describe description (Parameter.text \"value\" \"Value\" \"\")",
            "range" => "Parameter.intRange minimum 5 (Parameter.integer \"value\" \"Value\" 0)",
            "context" => "Parameter.bindContext contextBinding (Parameter.text \"value\" \"Value\" \"\")",
            _ => throw new ArgumentOutOfRangeException(nameof(modifierKind))
        };
        string script = $$"""
                          open Patchouli.Workflows
                          open Patchouli.Workflows.Scripting
                          let info = WorkflowInfo.create "Dynamic modifier" ""
                          let requiredMessage = "A required message."
                          let description = "A dynamic description."
                          let minimum = 0
                          let contextBinding = "documents"
                          let model = Parameter.model "model" "Model"
                          let value = {{modifier}}
                          let run : AgentWorkflow = Workflow.identity<WorkflowInput> |> Workflow.define |> Workflow.withModel model
                          """;

        WorkflowDeclarationAnalysis analysis =
            await new ScriptCompiler().AnalyzeWorkflowAsync(script, "dynamic-modifier.fsx", "run");

        analysis.Succeeded.Should().BeFalse(FormatDiagnostics(analysis));
        analysis.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.ErrorNumber == "WORKFLOW_DECLARATION_STATIC");
        analysis.Fields.Should().NotContain(field => field.Key == "value");
    }

    [Fact]
    public async Task Analyzer_rejects_top_level_parameter_values_constructed_by_helpers()
    {
        const string script = """
                              open Patchouli.Workflows
                              open Patchouli.Workflows.Scripting
                              let info = WorkflowInfo.create "Helper parameter" ""
                              let makeParameter key = Parameter.text key "Prompt" ""
                              let prompt = makeParameter "prompt"
                              let model = Parameter.model "model" "Model"
                              let run : AgentWorkflow = Workflow.identity<WorkflowInput> |> Workflow.define |> Workflow.withModel model
                              """;

        WorkflowDeclarationAnalysis analysis =
            await new ScriptCompiler().AnalyzeWorkflowAsync(script, "helper-parameter.fsx", "run");

        analysis.Succeeded.Should().BeFalse(FormatDiagnostics(analysis));
        analysis.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.ErrorNumber == "WORKFLOW_DECLARATION_STATIC");
        analysis.Fields.Should().NotContain(field => field.Key == "prompt");
    }

    [Fact]
    public async Task Analyzer_reports_dynamic_selection_scope_alias_as_static_info_error()
    {
        const string script = """
                              open Patchouli.Workflows
                              open Patchouli.Workflows.Scripting
                              let selectedScope = WorkflowSelectionScope.All
                              let info : WorkflowInfo =
                                  WorkflowInfo.create "Dynamic scope" ""
                                  |> WorkflowInfo.selectionScope selectedScope
                              let model = Parameter.model "model" "Model"
                              let run : AgentWorkflow = Workflow.identity<WorkflowInput> |> Workflow.define |> Workflow.withModel model
                              """;

        WorkflowDeclarationAnalysis analysis =
            await new ScriptCompiler().AnalyzeWorkflowAsync(script, "dynamic-scope.fsx", "run");

        analysis.Succeeded.Should().BeFalse(FormatDiagnostics(analysis));
        analysis.Diagnostics.Should().Contain(diagnostic => diagnostic.ErrorNumber == "WORKFLOW_INFO_STATIC");
    }

    [Fact]
    public async Task Analyzer_reports_menu_order_overflow_as_static_info_error()
    {
        const string script = """
                              open Patchouli.Workflows
                              open Patchouli.Workflows.Scripting
                              let info : WorkflowInfo =
                                  WorkflowInfo.create "Large menu order" ""
                                  |> WorkflowInfo.menu "Tools" 2147483648L
                              let model = Parameter.model "model" "Model"
                              let run : AgentWorkflow = Workflow.identity<WorkflowInput> |> Workflow.define |> Workflow.withModel model
                              """;

        WorkflowDeclarationAnalysis analysis =
            await new ScriptCompiler().AnalyzeWorkflowAsync(script, "large-menu-order.fsx", "run");

        analysis.Succeeded.Should().BeFalse(FormatDiagnostics(analysis));
        analysis.Diagnostics.Should().Contain(diagnostic => diagnostic.ErrorNumber == "WORKFLOW_INFO_STATIC");
    }

    [Fact]
    public async Task Resolver_preserves_blank_zero_false_defaults_and_validates_typed_values()
    {
        const string script = """
                              open Patchouli.Workflows
                              open Patchouli.Workflows.Scripting
                              let info = WorkflowInfo.create "Defaults" ""
                              let blank = Parameter.text "blank" "Blank" ""
                              let count = Parameter.integer "count" "Count" 0 |> Parameter.intRange 0 5
                              let enabled = Parameter.boolean "enabled" "Enabled" false
                              let model = Parameter.model "model" "Model"
                              let read (input: WorkflowInput) =
                                  Parameter.get blank input, Parameter.get count input, Parameter.get enabled input
                              let run : AgentWorkflow =
                                  Workflow.identity<WorkflowInput> |> Workflow.define |> Workflow.withModel model
                              """;

        WorkflowDeclarationAnalysis analysis =
            await new ScriptCompiler().AnalyzeWorkflowAsync(script, "defaults.fsx", "run");
        Dictionary<string, string> explicitValues = new();
        Dictionary<string, string> savedValues = new();
        Dictionary<string, string> contextValues = new();

        analysis.Succeeded.Should().BeTrue(FormatDiagnostics(analysis));
        ParameterResolutionResult result =
            WorkflowParameterResolver.ResolveParameters(analysis, explicitValues, savedValues, contextValues);

        result.Succeeded.Should().BeFalse();
        result.Values.Should().ContainKey("blank").WhoseValue.Should().BeEmpty();
        result.Values.Should().ContainKey("count").WhoseValue.Should().Be("0");
        result.Values.Should().ContainKey("enabled").WhoseValue.Should().Be("false");
        result.Issues.Should().Contain(issue => issue.Key == "model" && issue.Code == "WORKFLOW_PARAMETER_REQUIRED");

        explicitValues["count"] = "6";
        explicitValues["enabled"] = "maybe";
        ParameterResolutionResult invalid =
            WorkflowParameterResolver.ResolveParameters(analysis, explicitValues, savedValues, contextValues);
        invalid.Issues.Should().Contain(issue => issue.Key == "count" && issue.Code == "WORKFLOW_PARAMETER_RANGE");
        invalid.Issues.Should().Contain(issue => issue.Key == "enabled" && issue.Code == "WORKFLOW_PARAMETER_TYPE");
    }

    [Fact]
    public async Task Analyzer_recognizes_fully_qualified_sdk_calls_and_rejects_runtime_declaration_values()
    {
        const string script = """
                              let info = Patchouli.Workflows.Scripting.WorkflowInfo.create "Qualified" ""
                              let choice =
                                  Patchouli.Workflows.Scripting.Parameter.choice "mode" "Mode" [ "fast"; "safe" ] "fast"
                              let run : Patchouli.Workflows.Scripting.AgentWorkflow =
                                  Patchouli.Workflows.Scripting.Workflow.identity<Patchouli.Workflows.Scripting.WorkflowInput>
                                  |> Patchouli.Workflows.Scripting.Workflow.define
                              """;

        WorkflowDeclarationAnalysis analysis =
            await new ScriptCompiler().AnalyzeWorkflowAsync(script, "qualified.fsx", "run");

        analysis.Succeeded.Should().BeTrue(FormatDiagnostics(analysis));
        analysis.Fields.Should().ContainSingle(field => field.Key == "mode" && field.DefaultValue == "fast");
    }

    [Fact]
    public async Task Resolver_rejects_null_or_empty_required_documents_but_allows_empty_optional_lists()
    {
        const string script = """
                              open Patchouli.Workflows
                              open Patchouli.Workflows.Scripting
                              let info = WorkflowInfo.create "Documents" ""
                              let requiredDocuments = Parameter.documents "requiredDocuments" "Required documents" |> Parameter.required "Choose documents."
                              let optionalDocuments = Parameter.documents "optionalDocuments" "Optional documents"
                              let run : AgentWorkflow = Workflow.identity<WorkflowInput> |> Workflow.define
                              """;
        WorkflowDeclarationAnalysis analysis =
            await new ScriptCompiler().AnalyzeWorkflowAsync(script, "documents.fsx", "run");
        analysis.Succeeded.Should().BeTrue(FormatDiagnostics(analysis));

        foreach (string invalid in new[] { "null", "[null]", "[\"\"]" })
        {
            Dictionary<string, string> values = new()
            {
                ["requiredDocuments"] = invalid,
                ["optionalDocuments"] = "[]"
            };
            WorkflowParameterIssue[] issues = WorkflowParameterResolver.Validate(analysis, values);
            issues.Should().Contain(issue => issue.Key == "requiredDocuments" && issue.Code == "invalid_documents");
        }

        WorkflowParameterIssue[] emptyRequired = WorkflowParameterResolver.Validate(analysis,
            new Dictionary<string, string>
            {
                ["requiredDocuments"] = "[]",
                ["optionalDocuments"] = "[]"
            });
        emptyRequired.Should().Contain(issue =>
            issue.Key == "requiredDocuments" && issue.Code == "WORKFLOW_PARAMETER_REQUIRED");
        emptyRequired.Should().NotContain(issue => issue.Key == "optionalDocuments");
    }

    private static string FormatDiagnostics(WorkflowDeclarationAnalysis analysis)
    {
        return string.Join("\n", analysis.Diagnostics.Select(diagnostic =>
            $"{diagnostic.ErrorNumber} at {diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}"));
    }
}
