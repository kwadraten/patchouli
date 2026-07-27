using System.Text.Json.Serialization;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography.Biblatex;

public sealed record BiblatexHelperError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")]
    string Message);

public sealed record BiblatexPersonDto(
    [property: JsonPropertyName("family")] string? Family = null,
    [property: JsonPropertyName("given")] string? Given = null,
    [property: JsonPropertyName("prefix")] string? Prefix = null,
    [property: JsonPropertyName("suffix")] string? Suffix = null,
    [property: JsonPropertyName("literal")]
    string? Literal = null);

public sealed record BiblatexDateDto(
    [property: JsonPropertyName("years")] IReadOnlyList<int> Years,
    [property: JsonPropertyName("parts")] IReadOnlyList<IReadOnlyList<int>> Parts,
    [property: JsonPropertyName("literal")]
    string? Literal = null,
    [property: JsonPropertyName("circa")] bool Circa = false);

public sealed record BiblatexMalformedDto(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("message")]
    string Message);

public sealed record BiblatexVerifyDto(
    [property: JsonPropertyName("missing")]
    IReadOnlyList<string> Missing,
    [property: JsonPropertyName("superfluous")]
    IReadOnlyList<string> Superfluous,
    [property: JsonPropertyName("malformed")]
    IReadOnlyList<BiblatexMalformedDto> Malformed);

public sealed record BiblatexEntryDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("entry_type")]
    string EntryType,
    [property: JsonPropertyName("is_xdata")]
    bool IsXdata,
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string> Fields,
    [property: JsonPropertyName("persons")]
    IReadOnlyDictionary<string, IReadOnlyList<BiblatexPersonDto>> Persons,
    [property: JsonPropertyName("dates")] IReadOnlyDictionary<string, BiblatexDateDto> Dates,
    [property: JsonPropertyName("keywords")]
    IReadOnlyList<string> Keywords,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("verify_ok")]
    bool VerifyOk,
    [property: JsonPropertyName("verify")] BiblatexVerifyDto Verify);

public sealed record BiblatexWriteEntryDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("entry_type")]
    string EntryType,
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string> Fields,
    [property: JsonPropertyName("persons")]
    IReadOnlyDictionary<string, IReadOnlyList<BiblatexPersonDto>> Persons,
    [property: JsonPropertyName("keywords")]
    IReadOnlyList<string> Keywords);

public sealed record BiblatexHelperResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] BiblatexHelperError? Error = null,
    [property: JsonPropertyName("entries")]
    IReadOnlyList<BiblatexEntryDto>? Entries = null,
    [property: JsonPropertyName("text")] string? Text = null);

public sealed record BiblatexMappedItem(
    string ItemType,
    string? OriginalBiblatexEntryType,
    string Title,
    string? Subtitle,
    string? TitleShort,
    IReadOnlyList<ItemCreatorInput> Creators,
    IReadOnlyList<ItemDateInput> Dates,
    IReadOnlyList<ItemIdentifierInput> Identifiers,
    string? PublicationTitle,
    string? ContainerTitleShort,
    string? CollectionTitle,
    string? Publisher,
    string? Place,
    string? Edition,
    string? Genre,
    string? Number,
    string? ChapterNumber,
    string? Volume,
    string? Version,
    string? Issue,
    string? Pages,
    string? Language,
    string? Status,
    string? Note,
    string? AbstractText,
    IReadOnlyList<string> Tags,
    string? FilePath,
    string SourceEntryKey,
    string SourceEntryType);

public sealed record BiblatexMatchCandidate(
    string ItemId,
    string Title,
    string? PublicationTitle,
    string? Publisher,
    IReadOnlyList<string> AuthorKeys,
    IReadOnlySet<int> IssuedYears,
    int MatchCount,
    bool TitleMatched,
    bool AuthorsMatched,
    bool SourceMatched,
    bool YearMatched);

public sealed record BiblatexFieldConflict(
    string FieldKey,
    string Label,
    string? LocalValue,
    string IncomingValue);

public sealed record BiblatexSourceMatchGroup(
    BiblatexMappedItem Source,
    IReadOnlyList<BiblatexMatchCandidate> Candidates);

public interface IBiblatexHelperClient
{
    Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseAsync(
        string biblatexText,
        CancellationToken cancellationToken = default);

    Task<Result<string>> WriteAsync(
        IReadOnlyList<BiblatexWriteEntryDto> entries,
        CancellationToken cancellationToken = default);
}
