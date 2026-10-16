using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.FSharp.Core;
using Patchouli.Workflows;

namespace Patchouli.Host.Workflows;

/// <summary>Converts safe legacy metadata without evaluating or silently replacing unsupported scripts.</summary>
public sealed class WorkflowLegacyDeclarationMigrator(WorkflowStore store, ScriptCompiler compiler)
{
    private const string Marker = "// Patchouli SDK declaration migration v6";

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WorkflowDefinition> definitions = await store.ListDefinitionsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (WorkflowDefinition definition in definitions.Where(value => !value.BuiltIn && !value.Locked))
        {
            FSharpOption<string>? script =
                await store.ReadScriptAsync(definition.Id, cancellationToken).ConfigureAwait(false);
            if (script is null || script.Value.Contains(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            WorkflowDeclarationAnalysis existing = await compiler.AnalyzeWorkflowAsync(script.Value,
                store.ResolveScriptPath(definition.Id),
                definition.ScriptEntryPoint).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existing.Info.Name))
            {
                continue;
            }

            string? converted = Convert(definition, script.Value);
            if (converted is null)
            {
                continue;
            }

            // A textual rewrite is accepted only when the complete replacement passes the SDK AST analyzer.
            WorkflowDeclarationAnalysis analysis = await compiler.AnalyzeWorkflowAsync(converted,
                store.ResolveScriptPath(definition.Id),
                definition.ScriptEntryPoint).ConfigureAwait(false);
            if (!analysis.Succeeded)
            {
                continue;
            }

            string definitionPath = store.ResolveDefinitionPath(definition.Id);
            string backupPath = store.ResolveLegacyBackupPath(definition.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            if (!File.Exists(backupPath) && File.Exists(definitionPath))
            {
                File.Copy(definitionPath, backupPath);
            }

            string scriptBackup = backupPath + ".fsx";
            if (!File.Exists(scriptBackup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(scriptBackup)!);
                await File.WriteAllTextAsync(scriptBackup, script.Value, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
            }

            await store.SaveScriptAsync(definition.Id, converted, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? Convert(WorkflowDefinition definition, string script)
    {
        // Only a single ordinary, column-zero export is converted; nested/recursive/pattern exports need author repair.
        Regex entry = new("(?m)^let[ \t]+" + Regex.Escape(definition.ScriptEntryPoint) + "(?=[ \t:=])",
            RegexOptions.CultureInvariant);
        MatchCollection matches = entry.Matches(script);
        if (matches.Count != 1)
        {
            return null;
        }

        const string legacyEntry = "__patchouliLegacyEntry";
        const string modelVariable = "__patchouliMigratedModel";
        string modelKey = "executionModel";
        while (definition.Parameters.Any(parameter => parameter.Name == modelKey))
        {
            modelKey = "_" + modelKey;
        }

        StringBuilder prefix = new();
        prefix.AppendLine(Marker);
        prefix.AppendLine("open Patchouli.Workflows");
        prefix.AppendLine("open Patchouli.Workflows.Scripting");
        prefix.AppendLine("let info =");
        prefix.AppendLine($"    WorkflowInfo.create {Literal(definition.Name)} {Literal(definition.Description)}");
        prefix.AppendLine($"    |> WorkflowInfo.entryPoint {Literal(definition.ScriptEntryPoint)}");
        prefix.AppendLine($"    |> WorkflowInfo.selectionScope WorkflowSelectionScope.{definition.SelectionScope}");
        if (definition.Menu.ShowInMenu)
        {
            prefix.AppendLine($"    |> WorkflowInfo.menu {Literal(definition.Menu.MenuPath)} {definition.Menu.Order}");
        }

        prefix.AppendLine($"let {modelVariable} = Parameter.model {Literal(modelKey)} \"执行模型\"");
        for (int index = 0; index < definition.Parameters.Length; index++)
        {
            WorkflowParameter parameter = definition.Parameters[index];
            string? constructor = Constructor(parameter);
            if (constructor is null)
            {
                return null;
            }

            prefix.AppendLine($"let __patchouliLegacyParameter{index} =");
            prefix.AppendLine("    " + constructor);
            if (parameter.Required)
            {
                prefix.AppendLine($"    |> Parameter.required {Literal(parameter.Description)}");
            }
            else if (!string.IsNullOrWhiteSpace(parameter.Description))
            {
                prefix.AppendLine($"    |> Parameter.describe {Literal(parameter.Description)}");
            }

            if (parameter.Type == WorkflowParameterType.DocumentId)
            {
                prefix.AppendLine("    |> Parameter.bindContext \"documentId\"");
            }

            if (parameter.Type == WorkflowParameterType.PageRange)
            {
                prefix.AppendLine("    |> Parameter.bindContext \"pageRange\"");
            }
        }

        Match match = matches[0];
        string renamed = script[..match.Index] + "let " + legacyEntry + script[(match.Index + match.Length)..];
        return prefix + "\n" + renamed + $"\nlet {definition.ScriptEntryPoint} : AgentWorkflow =\n" +
               $"    {legacyEntry} |> Workflow.withModel {modelVariable}\n";
    }

    private static string? Constructor(WorkflowParameter parameter)
    {
        string arguments = Literal(parameter.Name) + " " + Literal(parameter.Name) + " ";
        string value = parameter.DefaultValue ?? "";
        return parameter.Type switch
        {
            WorkflowParameterType.Text or WorkflowParameterType.DocumentId =>
                "Parameter.text " + arguments + Literal(value),
            WorkflowParameterType.Language => "Parameter.language " + arguments + Literal(value),
            WorkflowParameterType.PageRange => "Parameter.pageRange " + arguments + Literal(value),
            WorkflowParameterType.Integer when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int integer) => "Parameter.integer " + arguments + integer.ToString(CultureInfo.InvariantCulture),
            WorkflowParameterType.Boolean when bool.TryParse(value, out bool boolean) =>
                "Parameter.boolean " + arguments + (boolean ? "true" : "false"),
            _ => null
        };
    }

    private static string Literal(string? value)
    {
        return JsonSerializer.Serialize(value ?? "");
    }
}
