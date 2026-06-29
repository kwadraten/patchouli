using System.Text;
using System.Text.Json;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Evidence;

public sealed record EvidenceReference(
    LibraryId LibraryId,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    SearchUnitId SearchUnitId,
    string TextRevisionId,
    string BboxRevisionId,
    LayoutRevisionId LayoutRevisionId,
    string? SnapshotId = null);

public sealed record EvidenceRefRecord(
    string EvidenceRecordId,
    string EvidenceRefId,
    LibraryId LibraryId,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    SearchUnitId SearchUnitId,
    string TextRevisionId,
    string BboxRevisionId,
    LayoutRevisionId LayoutRevisionId,
    string? SnapshotId,
    string PinnedText,
    string SourceTitle,
    string? PageLabel,
    int PageIndex,
    string Status,
    DateTimeOffset CreatedAt);

public static class EvidenceResolutionMode
{
    public const string Pinned = "pinned";
    public const string Current = "current";
    public const string Compare = "compare";
}

public static class EvidenceResolutionStatus
{
    public const string FoundPinned = "found_pinned";
    public const string FoundCurrent = "found_current";
    public const string Compared = "compared";
    public const string Superseded = "superseded";
    public const string Tombstoned = "tombstoned";
    public const string Purged = "purged";
    public const string NotFound = "not_found";
    public const string LibraryMismatch = "library_mismatch";
    public const string MultipleCurrentCandidates = "multiple_current_candidates";
    public const string InvalidRef = "invalid_ref";
}

public static class EvidenceRecordStatus
{
    public const string Active = "active";
    public const string Superseded = "superseded";
    public const string Tombstoned = "tombstoned";
    public const string Purged = "purged";
}

public static class EvidenceSuccessorReason
{
    public const string TextUpdated = "text_updated";
    public const string UnitSuperseded = "unit_superseded";
    public const string LayoutReplaced = "layout_replaced";
    public const string Manual = "manual";
}

public sealed record EvidenceResolutionResult(
    string Status,
    string EvidenceRefId,
    string? PinnedText,
    string? CurrentText,
    bool HasTextChanged,
    bool HasLayoutChanged,
    bool HasBboxChanged,
    string? SourceTitle,
    string? PageLabel,
    int? PageIndex,
    IReadOnlyList<string> SuccessorEvidenceRefs,
    string? ChainSummary,
    string? Warning);

public sealed record EvidenceMarkdown(
    string Markdown,
    string EvidenceRefId,
    string PinnedText,
    string SourceLine);

public static class EvidenceReferenceCodec
{
    private const string Prefix = "evref:v1:";

    public static Result<string> Encode(EvidenceReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.TextRevisionId) || string.IsNullOrWhiteSpace(reference.BboxRevisionId))
        {
            return Result<string>.Failure(AppErrorCodes.ValidationFailed, "Evidence revision ids are required.");
        }

        var payload = new Payload(
            1,
            reference.LibraryId.ToString(),
            reference.DocumentInstanceId.ToString(),
            reference.PageId.ToString(),
            reference.SearchUnitId.ToString(),
            reference.TextRevisionId,
            reference.BboxRevisionId,
            reference.LayoutRevisionId.ToString(),
            reference.SnapshotId);
        var json = JsonSerializer.Serialize(payload);
        return Result<string>.Success(Prefix + Base64UrlEncode(Encoding.UTF8.GetBytes(json)));
    }

    public static Result<EvidenceReference> Decode(string evidenceRefId)
    {
        if (string.IsNullOrWhiteSpace(evidenceRefId) || !evidenceRefId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Invalid("Evidence reference must start with evref:v1:.");
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(evidenceRefId[Prefix.Length..]));
            var payload = JsonSerializer.Deserialize<Payload>(json);
            if (payload is null || payload.V != 1
                || string.IsNullOrWhiteSpace(payload.LibraryId)
                || string.IsNullOrWhiteSpace(payload.DocumentInstanceId)
                || string.IsNullOrWhiteSpace(payload.PageId)
                || string.IsNullOrWhiteSpace(payload.UnitId)
                || string.IsNullOrWhiteSpace(payload.TextRevisionId)
                || string.IsNullOrWhiteSpace(payload.BboxRevisionId)
                || string.IsNullOrWhiteSpace(payload.LayoutRevisionId))
            {
                return Invalid("Evidence reference payload is incomplete.");
            }

            return Result<EvidenceReference>.Success(new EvidenceReference(
                LibraryId.Parse(payload.LibraryId),
                DocumentInstanceId.Parse(payload.DocumentInstanceId),
                PageId.Parse(payload.PageId),
                SearchUnitId.Parse(payload.UnitId),
                payload.TextRevisionId,
                payload.BboxRevisionId,
                LayoutRevisionId.Parse(payload.LayoutRevisionId),
                payload.SnapshotId));
        }
        catch
        {
            return Invalid("Evidence reference payload is invalid.");
        }
    }

    private static Result<EvidenceReference> Invalid(string message)
        => Result<EvidenceReference>.Failure(AppErrorCodes.InvalidEvidenceReference, message);

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private sealed record Payload(
        int V,
        string LibraryId,
        string DocumentInstanceId,
        string PageId,
        string UnitId,
        string TextRevisionId,
        string BboxRevisionId,
        string LayoutRevisionId,
        string? SnapshotId);
}
