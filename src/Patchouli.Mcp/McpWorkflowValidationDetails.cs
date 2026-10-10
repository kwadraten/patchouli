using System.Text.Json.Serialization;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>Portable workflow field diagnostics without local script paths or credentials.</summary>
public sealed record McpWorkflowValidationIssue(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")]
    string Message,
    [property: JsonPropertyName("line")] int? Line = null,
    [property: JsonPropertyName("column")] int? Column = null,
    [property: JsonPropertyName("scope_target")]
    string? ScopeTarget = null);

public sealed record McpWorkflowValidationFailureDetails(IReadOnlyList<McpWorkflowValidationIssue> Issues)
    : IResultFailureDetails
{
    public string Kind => "workflow_validation";
}
