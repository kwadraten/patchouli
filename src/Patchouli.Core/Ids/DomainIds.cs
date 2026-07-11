namespace Patchouli.Core.Ids;

public readonly record struct LibraryId(Guid Value)
{
    public static LibraryId New()
    {
        return new LibraryId(Guid.NewGuid());
    }

    public static LibraryId Parse(string value)
    {
        return new LibraryId(Guid.Parse(value));
    }

    public static bool TryParse(string? value, out LibraryId libraryId)
    {
        if (Guid.TryParse(value, out Guid guid))
        {
            libraryId = new LibraryId(guid);
            return true;
        }

        libraryId = default;
        return false;
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ItemId(Guid Value)
{
    public static ItemId New()
    {
        return new ItemId(Guid.NewGuid());
    }

    public static ItemId Parse(string value)
    {
        return new ItemId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct IdentifierId(Guid Value)
{
    public static IdentifierId New()
    {
        return new IdentifierId(Guid.NewGuid());
    }

    public static IdentifierId Parse(string value)
    {
        return new IdentifierId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct FileAssetId(Guid Value)
{
    public static FileAssetId New()
    {
        return new FileAssetId(Guid.NewGuid());
    }

    public static FileAssetId Parse(string value)
    {
        return new FileAssetId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct FileSearchRootId(Guid Value)
{
    public static FileSearchRootId New()
    {
        return new FileSearchRootId(Guid.NewGuid());
    }

    public static FileSearchRootId Parse(string value)
    {
        return new FileSearchRootId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct KnownFileLocationId(Guid Value)
{
    public static KnownFileLocationId New()
    {
        return new KnownFileLocationId(Guid.NewGuid());
    }

    public static KnownFileLocationId Parse(string value)
    {
        return new KnownFileLocationId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct DocumentInstanceId(Guid Value)
{
    public static DocumentInstanceId New()
    {
        return new DocumentInstanceId(Guid.NewGuid());
    }

    public static DocumentInstanceId Parse(string value)
    {
        return new DocumentInstanceId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct PageId(Guid Value)
{
    public static PageId New()
    {
        return new PageId(Guid.NewGuid());
    }

    public static PageId Parse(string value)
    {
        return new PageId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct LayoutRevisionId(Guid Value)
{
    public static LayoutRevisionId New()
    {
        return new LayoutRevisionId(Guid.NewGuid());
    }

    public static LayoutRevisionId Parse(string value)
    {
        return new LayoutRevisionId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct LayoutNodeId(Guid Value)
{
    public static LayoutNodeId New()
    {
        return new LayoutNodeId(Guid.NewGuid());
    }

    public static LayoutNodeId Parse(string value)
    {
        return new LayoutNodeId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct SearchUnitId(Guid Value)
{
    public static SearchUnitId New()
    {
        return new SearchUnitId(Guid.NewGuid());
    }

    public static SearchUnitId Parse(string value)
    {
        return new SearchUnitId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct EvidenceRefId(Guid Value)
{
    public static EvidenceRefId New()
    {
        return new EvidenceRefId(Guid.NewGuid());
    }

    public static EvidenceRefId Parse(string value)
    {
        return new EvidenceRefId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct CredentialId(Guid Value)
{
    public static CredentialId New()
    {
        return new CredentialId(Guid.NewGuid());
    }

    public static CredentialId Parse(string value)
    {
        return new CredentialId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct OcrPresetId(Guid Value)
{
    public static OcrPresetId New()
    {
        return new OcrPresetId(Guid.NewGuid());
    }

    public static OcrPresetId Parse(string value)
    {
        return new OcrPresetId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct OcrPresetVersionId(Guid Value)
{
    public static OcrPresetVersionId New()
    {
        return new OcrPresetVersionId(Guid.NewGuid());
    }

    public static OcrPresetVersionId Parse(string value)
    {
        return new OcrPresetVersionId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct OcrRunId(Guid Value)
{
    public static OcrRunId New()
    {
        return new OcrRunId(Guid.NewGuid());
    }

    public static OcrRunId Parse(string value)
    {
        return new OcrRunId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct OcrPageResultId(Guid Value)
{
    public static OcrPageResultId New()
    {
        return new OcrPageResultId(Guid.NewGuid());
    }

    public static OcrPageResultId Parse(string value)
    {
        return new OcrPageResultId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct OcrCandidateAdoptionId(Guid Value)
{
    public static OcrCandidateAdoptionId New()
    {
        return new OcrCandidateAdoptionId(Guid.NewGuid());
    }

    public static OcrCandidateAdoptionId Parse(string value)
    {
        return new OcrCandidateAdoptionId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct OcrQueueTaskId(Guid Value)
{
    public static OcrQueueTaskId New()
    {
        return new OcrQueueTaskId(Guid.NewGuid());
    }

    public static OcrQueueTaskId Parse(string value)
    {
        return new OcrQueueTaskId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct SearchProfileId(Guid Value)
{
    public static SearchProfileId New()
    {
        return new SearchProfileId(Guid.NewGuid());
    }

    public static SearchProfileId Parse(string value)
    {
        return new SearchProfileId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct SearchRewriteRuleId(Guid Value)
{
    public static SearchRewriteRuleId New()
    {
        return new SearchRewriteRuleId(Guid.NewGuid());
    }

    public static SearchRewriteRuleId Parse(string value)
    {
        return new SearchRewriteRuleId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct BlockingOperationId(Guid Value)
{
    public static BlockingOperationId New()
    {
        return new BlockingOperationId(Guid.NewGuid());
    }

    public static BlockingOperationId Parse(string value)
    {
        return new BlockingOperationId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct BlockingOperationLogEntryId(Guid Value)
{
    public static BlockingOperationLogEntryId New()
    {
        return new BlockingOperationLogEntryId(Guid.NewGuid());
    }

    public static BlockingOperationLogEntryId Parse(string value)
    {
        return new BlockingOperationLogEntryId(Guid.Parse(value));
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
