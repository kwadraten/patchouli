using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Evidence;

/// <summary>Creates and resolves stable text-only evidence references over persisted search units.</summary>
public interface IEvidenceReferenceService
{
    Task<Result<EvidenceRefRecord>> CreateFromSearchUnitAsync(SearchUnitId unitId, CancellationToken cancellationToken = default);
    Task<Result<EvidenceResolutionResult>> ResolveAsync(string evidenceRefId, string mode = EvidenceResolutionMode.Pinned, CancellationToken cancellationToken = default);
    Task<Result<EvidenceMarkdown>> CreateMarkdownAsync(string evidenceRefId, CancellationToken cancellationToken = default);
    Task<Result> MarkSupersededAsync(string evidenceRefId, string successorEvidenceRefId, string reason, CancellationToken cancellationToken = default);
    Task<Result> TombstoneAsync(string evidenceRefId, string reason, CancellationToken cancellationToken = default);
    Task<Result> PurgeAsync(string evidenceRefId, string reason, CancellationToken cancellationToken = default);
}
