using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography.MetadataLookup;

public static class MetadataLookupErrorCodes
{
    public const string UnsupportedIdentifier = "metadata_unsupported_identifier";
    public const string NotFound = "metadata_not_found";
    public const string RateLimited = "metadata_rate_limited";
    public const string QuotaUnavailable = "metadata_quota_unavailable";
    public const string Timeout = "metadata_timeout";
    public const string InvalidResponse = "metadata_invalid_response";
    public const string ProviderUnavailable = "metadata_provider_unavailable";
}

public sealed record NormalizedIdentifier(string Scheme, string Value);

public sealed record MetadataIdentifier(string Scheme, string Value);

public sealed record MetadataCreator(
    string Role,
    string? Family = null,
    string? Given = null,
    string? Literal = null,
    string? Suffix = null,
    string? Particles = null);

public sealed record MetadataDate(
    string Role,
    IReadOnlyList<int>? DateParts = null,
    string? Literal = null,
    bool Circa = false,
    string? Season = null);

public sealed record MetadataCandidate(
    string SourceId,
    string? Title = null,
    string? Subtitle = null,
    string? TitleShort = null,
    IReadOnlyList<MetadataCreator>? Creators = null,
    IReadOnlyList<MetadataDate>? Dates = null,
    string? PublicationTitle = null,
    string? ContainerTitleShort = null,
    string? CollectionTitle = null,
    string? Publisher = null,
    string? Place = null,
    string? Edition = null,
    string? Genre = null,
    string? Number = null,
    string? ChapterNumber = null,
    string? Volume = null,
    string? Version = null,
    string? Issue = null,
    string? Pages = null,
    string? Language = null,
    string? Status = null,
    string? Note = null,
    string? Abstract = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<MetadataIdentifier>? Identifiers = null,
    string? SuggestedItemType = null,
    double TypeConfidence = 0);

public sealed record MetadataSourceDefinition(
    string Id,
    string DisplayName,
    IReadOnlySet<string> SupportedSchemes,
    int DefaultPriority,
    bool DefaultEnabled = true);

public sealed record MetadataSourcePreference(string SourceId, bool Enabled, int Priority);

public interface IMetadataSource
{
    MetadataSourceDefinition Definition { get; }

    Task<Result<MetadataCandidate>> LookupAsync(
        NormalizedIdentifier identifier,
        CancellationToken cancellationToken = default);
}

public interface IMetadataSourceRegistry
{
    IReadOnlyList<MetadataSourceDefinition> Sources { get; }

    IReadOnlyList<IMetadataSource> Resolve(
        string identifierScheme,
        IReadOnlyList<MetadataSourcePreference>? preferences = null);
}

public sealed record MetadataLookupAttempt(string SourceId, string? ErrorCode, string? ErrorMessage);

public sealed record MetadataLookupOutcome(
    ItemMetadata Item,
    MetadataCandidate Candidate,
    IReadOnlyList<MetadataLookupAttempt> Attempts,
    IReadOnlyList<ItemIdentifier> AddedIdentifiers);

public sealed record MetadataBatchProgress(
    int Completed,
    int Total,
    int Succeeded,
    int Failed,
    string? Message = null);

public sealed record MetadataBatchItemResult(
    ItemId ItemId,
    bool IsSuccess,
    string? SourceId,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record MetadataBatchResult(
    int SucceededCount,
    int FailedCount,
    IReadOnlyList<MetadataBatchItemResult> Items);

public interface IMetadataLookupService
{
    bool CanLookup(string scheme);

    Task<Result<MetadataLookupOutcome>> LookupAndApplyAsync(
        ItemId itemId,
        ItemIdentifier identifier,
        CancellationToken cancellationToken = default);

    Task<Result<MetadataLookupOutcome>> LookupAndMergeAsync(
        ItemId itemId,
        string scheme,
        string value,
        IReadOnlyList<MetadataSourcePreference>? preferences = null,
        CancellationToken cancellationToken = default);

    Task<Result<MetadataBatchResult>> LookupAndApplyBatchAsync(
        IReadOnlyList<ItemId> itemIds,
        IProgress<MetadataBatchProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
