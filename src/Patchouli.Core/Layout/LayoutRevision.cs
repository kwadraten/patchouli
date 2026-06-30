using Patchouli.Core.Ids;

namespace Patchouli.Core.Layout;

public sealed record LayoutRevision(
    LayoutRevisionId LayoutRevisionId,
    DocumentInstanceId DocumentInstanceId,
    LayoutRevisionId? ParentRevisionId,
    string Source,
    bool IsCurrent,
    DateTimeOffset CreatedAt);
