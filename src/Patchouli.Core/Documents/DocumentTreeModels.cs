using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

namespace Patchouli.Core.Documents;

public sealed record DocumentTreeRevision(
    DocumentTreeRevisionId TreeRevisionId,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    DocumentTreeRevisionId? ParentTreeRevisionId,
    string Source,
    string Status,
    bool IsCurrent,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CommittedAt,
    DocumentTreeRevisionId? RevertedFromTreeRevisionId = null);

public static class DocumentTreeRevisionSource
{
    public const string Import = "import";
    public const string ManualEdit = "manual_edit";

    /// <summary>
    /// Produced when an OCR result is committed. Kept for legacy data compatibility;
    /// new OCR commits continue to use this value.
    /// </summary>
    public const string OcrAdopted = "ocr_adopted";

    public const string Migration = "migration";
    public const string Revert = "revert";

    public static bool IsKnown(string value)
    {
        return value is Import or ManualEdit or OcrAdopted or Migration or Revert;
    }
}

/// <summary>
/// Working-copy and immutable committed revision status model.
/// Legacy values ('staging', 'draft', 'discarded') may still exist in old user databases,
/// but they are never read by the application. This helper treats them as "not known"
/// for writing; read paths filter them out in SQL rather than relying on this record.
/// The C# record simply carries the raw string value so that legacy rows do not crash reads.
/// </summary>
public static class DocumentTreeRevisionStatus
{
    public const string Working = "working";
    public const string Committed = "committed";

    public static bool IsKnown(string value)
    {
        return value is Working or Committed;
    }
}

public sealed record DocumentBox(
    DocumentTreeRevisionId TreeRevisionId,
    DocumentBoxId BoxId,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    DocumentBoxId? ParentBoxId,
    DocumentBoxId? NextSiblingBoxId,
    string BoxType,
    string? SubType,
    NormalizedBBox BBox,
    DocumentBoxPayload? Payload,
    int? HeadingLevel,
    string? CodeLanguage,
    double? Confidence,
    bool Suppressed,
    DocumentBoxId? ContinuesFromBoxId = null,
    string? ContinuationState = null);

/// <summary>
/// 接续关系的兼容状态。标准接续（MinerU v4 <c>continues_prev</c> 显式关系或工作台人工建立）
/// 不带状态值；只有旧版 MinerU 空框推断产生、且本框文字未恢复的接续才标记为
/// <see cref="LegacyUnrecovered"/>，表示全文仍保留在源框、本框尚无自己的文字。
/// </summary>
public static class DocumentContinuationState
{
    public const string LegacyUnrecovered = "legacy_unrecovered";

    public static bool IsKnown(string? value)
    {
        return value is null or LegacyUnrecovered;
    }
}

/// <summary>
/// 接续（<c>continues_prev</c>）的统一合法性约束：只有正文类物理框可以接续，
/// 且后框必须与源框同类型。导入、工作台编辑与 XML 往返共用同一套规则。
/// </summary>
public static class DocumentContinuationRules
{
    public static bool IsEligibleBoxType(string boxType)
    {
        return boxType is DocumentBoxType.Text or DocumentBoxType.RefText or DocumentBoxType.List
            or DocumentBoxType.Table;
    }

    public static bool CanContinue(string followerBoxType, string sourceBoxType)
    {
        return IsEligibleBoxType(followerBoxType) && followerBoxType == sourceBoxType;
    }
}

public abstract record DocumentBoxPayload
{
    [System.Text.Json.Serialization.JsonIgnore(Condition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DocumentAnnotation>? Annotations { get; init; }
}

public sealed record DocumentAnnotation(string Type, string Markdown);

public sealed record TextBoxPayload(string Markdown) : DocumentBoxPayload;

public sealed record EquationBoxPayload(string Latex, bool HasImage = false) : DocumentBoxPayload;

public sealed record ListBoxPayload(string Markdown) : DocumentBoxPayload;

public static class TableContentFormat
{
    public const string LegacyMarkdown = "markdown";
    public const string Html = "html";
    public const int AuthoritativeHtmlVersion = 1;
}

/// <summary>
/// A table payload. The original positional constructor remains the legacy Markdown form.
/// Versioned HTML payloads keep their editable content only in <see cref="Html"/>; Markdown
/// is empty and all projections are produced by <see cref="TableContentAdapter"/>.
/// </summary>
public sealed record TableBoxPayload(string Markdown, string? Html = null) : DocumentBoxPayload
{
    public string ContentFormat { get; init; } = TableContentFormat.LegacyMarkdown;

    public int ContentFormatVersion { get; init; }
}

public sealed record AlgorithmBoxPayload(string Markdown) : DocumentBoxPayload;

public sealed record CodeBoxPayload(string Code) : DocumentBoxPayload;

public sealed record MediaBoxPayload(string? AssetId, string? Description) : DocumentBoxPayload;

public static class DocumentBoxType
{
    public const string LogicalPage = "logical_page";
    public const string Text = "text";
    public const string Title = "title";
    public const string RefText = "ref_text";

    public const string Equation = "equation";

    // Read compatibility: older MinerU v2 imports stored this unknown type with empty text.
    // New imports must use Equation with an EquationBoxPayload.
    public const string LegacyEquationInterline = "equation_interline";
    public const string List = "list";
    public const string Index = "index";
    public const string Image = "image";
    public const string Table = "table";
    public const string Chart = "chart";
    public const string Code = "code";
    public const string Algorithm = "algorithm";
    public const string ImageCaption = "image_caption";
    public const string ImageFootnote = "image_footnote";
    public const string TableCaption = "table_caption";
    public const string TableFootnote = "table_footnote";
    public const string ChartCaption = "chart_caption";
    public const string ChartFootnote = "chart_footnote";
    public const string CodeCaption = "code_caption";
    public const string CodeFootnote = "code_footnote";
    public const string Header = "header";
    public const string Footer = "footer";
    public const string PageNumber = "page_number";
    public const string AsideText = "aside_text";
    public const string PageFootnote = "page_footnote";

    private static readonly HashSet<string> KnownTypes =
    [
        LogicalPage, Text, Title, RefText, Equation, List, Index, Image, Table, Chart, Code,
        ImageCaption, ImageFootnote, TableCaption, TableFootnote, ChartCaption, ChartFootnote,
        CodeCaption, CodeFootnote, Header, Footer, PageNumber, AsideText, PageFootnote
    ];

    private static readonly HashSet<string> AuxiliaryTypes = [Header, Footer, PageNumber, AsideText, PageFootnote];

    private static readonly HashSet<string> OverlapTypes =
        ["phonetic", "ruby", "warichu", "annotation", "aside", "seal"];

    public static bool IsValidSubType(string type, string? subType)
    {
        return subType is null || type switch
        {
            Code => subType is Code or Algorithm,
            List => subType is Text or RefText,
            Image or Chart => !string.IsNullOrWhiteSpace(subType),
            _ => false
        };
    }

    public static bool IsKnown(string value)
    {
        return KnownTypes.Contains(value);
    }

    public static bool IsAuxiliary(string value)
    {
        return AuxiliaryTypes.Contains(value);
    }

    public static bool AllowsOverlap(string value)
    {
        return OverlapTypes.Contains(value);
    }
}

public sealed record PageEditSession(
    PageEditSessionId SessionId,
    DocumentTreeRevisionId DraftRevisionId,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId);

public sealed record InsertLeafCommand(
    DocumentBoxId? ParentBoxId,
    DocumentBoxId? InsertAfterBoxId,
    string BoxType,
    string? SubType,
    NormalizedBBox BBox,
    DocumentBoxPayload Payload,
    int? HeadingLevel = null,
    string? CodeLanguage = null,
    double? Confidence = null,
    bool Suppressed = false,
    DocumentBoxId? BoxId = null);

public sealed record UpdateLeafCommand(
    DocumentBoxId BoxId,
    string BoxType,
    DocumentBoxPayload Payload,
    int? HeadingLevel = null,
    string? CodeLanguage = null,
    string? SubType = null);

public sealed record MoveBoxCommand(
    DocumentBoxId BoxId,
    DocumentBoxId? NewParentBoxId,
    DocumentBoxId? InsertAfterBoxId);

public sealed record SplitLeafCommand(
    DocumentBoxId BoxId,
    NormalizedBBox FirstBBox,
    DocumentBoxPayload FirstPayload,
    NormalizedBBox SecondBBox,
    DocumentBoxPayload SecondPayload);

public sealed record MergeLeavesCommand(
    IReadOnlyList<DocumentBoxId> BoxIds,
    DocumentBoxPayload Payload);

public sealed record LocalOcrCandidate(
    string BoxType,
    DocumentBoxPayload Payload,
    int? HeadingLevel);

public sealed record DocumentBoxSeed(
    DocumentBoxId? BoxId,
    DocumentBoxId? ParentBoxId,
    int SourceOrder,
    string BoxType,
    string? SubType,
    NormalizedBBox BBox,
    DocumentBoxPayload? Payload,
    int? HeadingLevel = null,
    string? CodeLanguage = null,
    double? Confidence = null,
    bool Suppressed = false,
    DocumentBoxId? ContinuesFromBoxId = null,
    string? ContinuationState = null);

/// <summary>
/// A document-wide commit that groups one committed revision per page.
/// HEAD is the latest commit for the document (highest created_at / commit_id order).
/// </summary>
public sealed record DocumentCommit(
    DocumentCommitId CommitId,
    DocumentInstanceId DocumentInstanceId,
    DocumentCommitId? ParentCommitId,
    string Source,
    string? Message,
    DateTimeOffset CreatedAt);

/// <summary>
/// Links a page revision into a document-wide commit.
/// <paramref name="RevertedFromTreeRevisionId"/> is set when the page revision was
/// produced by a revert, pointing at the revision it restored.
/// </summary>
public sealed record DocumentCommitPage(
    DocumentCommitId CommitId,
    PageId PageId,
    DocumentTreeRevisionId TreeRevisionId,
    DocumentTreeRevisionId? RevertedFromTreeRevisionId = null);

/// <summary>
/// A document commit together with the page-to-revision mappings that belong to it.
/// </summary>
public sealed record DocumentCommitDetail(
    DocumentCommit Commit,
    IReadOnlyList<DocumentCommitPage> Pages);
