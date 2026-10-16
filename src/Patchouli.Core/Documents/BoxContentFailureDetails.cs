using Patchouli.Core.Results;

namespace Patchouli.Core.Documents;

public sealed record BoxContentError(string BoxId, string Message);

/// <summary>Identifies rejected members of an atomic box replacement batch.</summary>
public sealed record BoxContentFailureDetails(IReadOnlyList<BoxContentError> Errors) : IResultFailureDetails
{
    public string Kind => "box_content_mismatch";
}
