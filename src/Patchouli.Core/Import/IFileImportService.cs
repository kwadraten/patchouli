using Patchouli.Core.Results;

namespace Patchouli.Core.Import;

/// <summary>
/// Issue #11: shared import core used by MCP <c>add</c>, the CLI, scan import and window
/// drag-drop. Action entries pick the file handling behavior; this service owns path
/// validation, file handling, fingerprinting, dedup and ingestion.
/// </summary>
public interface IFileImportService
{
    /// <summary>
    /// Validates the local path, applies the trigger's file handling behavior, and imports
    /// the file. Never returns success on failure; never exposes host source paths.
    /// </summary>
    Task<Result<FileImportResult>> ImportAsync(
        FileImportRequest request,
        CancellationToken cancellationToken = default);
}
