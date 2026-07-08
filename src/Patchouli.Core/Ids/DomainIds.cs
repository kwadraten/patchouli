namespace Patchouli.Core.Ids;

public readonly record struct LibraryId(Guid Value)
{
    public static LibraryId New() => new(Guid.NewGuid());
    public static LibraryId Parse(string value) => new(Guid.Parse(value));
    public static bool TryParse(string? value, out LibraryId libraryId)
    {
        if (Guid.TryParse(value, out var guid))
        {
            libraryId = new LibraryId(guid);
            return true;
        }

        libraryId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ItemId(Guid Value)
{
    public static ItemId New() => new(Guid.NewGuid());
    public static ItemId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct IdentifierId(Guid Value)
{
    public static IdentifierId New() => new(Guid.NewGuid());
    public static IdentifierId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct FileAssetId(Guid Value)
{
    public static FileAssetId New() => new(Guid.NewGuid());
    public static FileAssetId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct FileSearchRootId(Guid Value)
{
    public static FileSearchRootId New() => new(Guid.NewGuid());
    public static FileSearchRootId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct KnownFileLocationId(Guid Value)
{
    public static KnownFileLocationId New() => new(Guid.NewGuid());
    public static KnownFileLocationId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct DocumentInstanceId(Guid Value)
{
    public static DocumentInstanceId New() => new(Guid.NewGuid());
    public static DocumentInstanceId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct PageId(Guid Value)
{
    public static PageId New() => new(Guid.NewGuid());
    public static PageId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct LayoutRevisionId(Guid Value)
{
    public static LayoutRevisionId New() => new(Guid.NewGuid());
    public static LayoutRevisionId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct LayoutNodeId(Guid Value)
{
    public static LayoutNodeId New() => new(Guid.NewGuid());
    public static LayoutNodeId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct SearchUnitId(Guid Value)
{
    public static SearchUnitId New() => new(Guid.NewGuid());
    public static SearchUnitId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct EvidenceRefId(Guid Value)
{
    public static EvidenceRefId New() => new(Guid.NewGuid());
    public static EvidenceRefId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct CredentialId(Guid Value)
{
    public static CredentialId New() => new(Guid.NewGuid());
    public static CredentialId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct OcrPresetId(Guid Value)
{
    public static OcrPresetId New() => new(Guid.NewGuid());
    public static OcrPresetId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct OcrPresetVersionId(Guid Value)
{
    public static OcrPresetVersionId New() => new(Guid.NewGuid());
    public static OcrPresetVersionId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct OcrRunId(Guid Value)
{
    public static OcrRunId New() => new(Guid.NewGuid());
    public static OcrRunId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct OcrPageResultId(Guid Value)
{
    public static OcrPageResultId New() => new(Guid.NewGuid());
    public static OcrPageResultId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct OcrCandidateAdoptionId(Guid Value)
{
    public static OcrCandidateAdoptionId New() => new(Guid.NewGuid());
    public static OcrCandidateAdoptionId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct OcrQueueTaskId(Guid Value)
{
    public static OcrQueueTaskId New() => new(Guid.NewGuid());
    public static OcrQueueTaskId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct SearchProfileId(Guid Value)
{
    public static SearchProfileId New() => new(Guid.NewGuid());
    public static SearchProfileId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct SearchRewriteRuleId(Guid Value)
{
    public static SearchRewriteRuleId New() => new(Guid.NewGuid());
    public static SearchRewriteRuleId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct BlockingOperationId(Guid Value)
{
    public static BlockingOperationId New() => new(Guid.NewGuid());
    public static BlockingOperationId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct BlockingOperationLogEntryId(Guid Value)
{
    public static BlockingOperationLogEntryId New() => new(Guid.NewGuid());
    public static BlockingOperationLogEntryId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}
