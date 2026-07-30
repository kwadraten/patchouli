using Patchouli.Core.Results;

namespace Patchouli.Core.Import;

public static class FirstRunStep
{
    public const string Database = "database";
    public const string Library = "library";
    public const string Scan = "scan";
    public const string Import = "import";
    public const string MinerUConfig = "mineru_config";
    public const string Extract = "extract";
    public const string Index = "index";
    public const string McpVerify = "mcp_verify";
    public const string Complete = "complete";
}

public sealed record FirstRunWorkflowState(
    string CurrentStep,
    string ProgressText,
    string? SelectedPdfPath,
    string? CreatedLibraryId,
    string? CreatedItemId,
    string? CreatedFileAssetId,
    string? CreatedDocumentInstanceId,
    string? LastError,
    bool IsComplete) : IOperationOutcome
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(LastError);
    public string? ErrorMessage => LastError;

    public static FirstRunWorkflowState Initial()
    {
        return new FirstRunWorkflowState(FirstRunStep.Database, "Select database location", null, null, null, null,
            null, null, false);
    }
}

public sealed record MinerUConfiguration(
    string Token,
    string? BaseUrl,
    string? ModelVersion,
    bool IsOcr,
    bool EnableTable,
    bool EnableFormula,
    int PollingTimeoutSeconds);

public sealed record McpVerificationResult(
    bool IsSearchable,
    string IndexStatus,
    int MatchedUnitCount,
    string? SampleText,
    string? ErrorMessage);
