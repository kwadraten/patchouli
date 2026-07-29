using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Bibliography;

public sealed class CslItemTypeProfileService : ICslItemTypeProfileService
{
    private static readonly IReadOnlyDictionary<string, CslItemTypeProfile> Profiles =
        new[]
            {
                Create(
                    "general",
                    "Patchouli-specific catch-all type for incomplete or not-yet-classified items.",
                    ["title"],
                    ["author", "issued", "language", "abstract"],
                    [],
                    [.. ItemCreatorRoles.Supported.OrderBy(static role => role, StringComparer.Ordinal)],
                    [.. ItemDateRoles.Supported.OrderBy(static role => role, StringComparer.Ordinal)],
                    [
                        BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.ISBN, BuiltInIdentifierSchemes.ISSN,
                        BuiltInIdentifierSchemes.URL
                    ],
                    ["container-title-short", "archive"],
                    renderableInCsl: false),
                Create(
                    "article",
                    "Preprints, working papers and other article manuscripts.",
                    ["title", "author"],
                    ["issued", "genre", "collection-title", "publisher"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.Editor, ItemCreatorRoles.Translator],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL]),
                Create(
                    "article-journal",
                    "Articles published in journals.",
                    ["title", "author", "container-title"],
                    ["container-title-short", "issued", "volume", "issue", "pages"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.Editor, ItemCreatorRoles.Translator],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.ISSN, BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["container-title"] = "期刊名",
                        ["number"] = "期号"
                    }),
                Create(
                    "article-magazine",
                    "Articles published in magazines.",
                    ["title", "author", "container-title"],
                    ["issued", "volume", "issue", "pages"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["container-title"] = "杂志名"
                    }),
                Create(
                    "article-newspaper",
                    "Articles published in newspapers.",
                    ["title", "author", "container-title"],
                    ["issued", "edition", "section", "pages", "publisher-place"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["container-title"] = "报纸名"
                    }),
                Create(
                    "bill",
                    "Proposed legislation.",
                    ["title"],
                    ["authority", "jurisdiction", "number", "section", "issued", "status", "references"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "book",
                    "Monographs and books.",
                    ["title", "author", "publisher"],
                    ["issued", "publisher-place", "edition", "volume", "number-of-volumes", "collection-title"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.Editor, ItemCreatorRoles.Translator],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.ISBN, BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL]),
                Create(
                    "broadcast",
                    "Radio and television programs.",
                    ["title", "container-title"],
                    ["issued", "genre", "medium", "publisher"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.Host, ItemCreatorRoles.Producer],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "chapter",
                    "Chapters within edited volumes.",
                    ["title", "author", "container-title"],
                    ["issued", "publisher", "publisher-place", "edition", "volume", "pages", "chapter-number"],
                    [],
                    [
                        ItemCreatorRoles.Author, ItemCreatorRoles.ContainerAuthor, ItemCreatorRoles.Editor,
                        ItemCreatorRoles.Translator
                    ],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.ISBN, BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["container-title"] = "文献出处"
                    }),
                Create(
                    "classic",
                    "Classical works.",
                    ["title", "author"],
                    ["title-short", "original-date", "issued", "publisher", "publisher-place", "volume", "pages"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.Editor, ItemCreatorRoles.Translator],
                    [ItemDateRoles.Issued, ItemDateRoles.OriginalDate, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "collection",
                    "Archival collections.",
                    ["title"],
                    ["archive", "archive-place", "archive_collection", "archive_location", "issued"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.CallNumber, BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["call-number"] = "档案号"
                    }),
                Create(
                    "dataset",
                    "Research datasets.",
                    ["title", "author"],
                    ["issued", "version", "publisher", "publisher-place", "medium", "license"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL]),
                Create(
                    "document",
                    "Generic documents without a more specific type.",
                    ["title", "author"],
                    ["issued", "genre", "publisher", "publisher-place", "number"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "entry",
                    "Entries in reference works.",
                    ["title", "author", "container-title"],
                    ["issued", "publisher", "accessed"],
                    [],
                    dateRoles: [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "entry-dictionary",
                    "Dictionary entries.",
                    ["title", "author", "container-title"],
                    ["issued", "publisher", "publisher-place", "edition", "volume", "pages"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.Editor],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "entry-encyclopedia",
                    "Encyclopedia entries.",
                    ["title", "author", "container-title"],
                    ["issued", "publisher", "publisher-place", "edition", "volume", "pages"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.Editor],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "event",
                    "Events such as conferences or exhibitions.",
                    ["title"],
                    ["event-title", "event-place", "event-date", "publisher"],
                    [],
                    [ItemCreatorRoles.Organizer],
                    [ItemDateRoles.EventDate],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "figure",
                    "Figures and illustrations.",
                    ["title", "author"],
                    ["container-title", "issued", "pages", "number", "medium"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "graphic",
                    "Visual artworks.",
                    ["title", "author"],
                    ["issued", "medium", "dimensions", "archive", "archive_location", "publisher"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "hearing",
                    "Official hearings.",
                    ["title"],
                    ["authority", "jurisdiction", "number", "section", "event-date", "event-place", "publisher"],
                    [],
                    dateRoles: [ItemDateRoles.EventDate, ItemDateRoles.Issued],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "interview",
                    "Interviews in any medium.",
                    ["title", "author"],
                    ["issued", "genre", "medium", "container-title", "publisher"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.Interviewer],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["author"] = "受访者"
                    }),
                Create(
                    "legal_case",
                    "Court cases and decisions.",
                    ["title"],
                    [
                        "authority", "jurisdiction", "division", "number", "issued", "container-title", "volume",
                        "pages",
                        "references"
                    ],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "legislation",
                    "Laws and statutes.",
                    ["title"],
                    ["authority", "jurisdiction", "number", "section", "issued", "references"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "manuscript",
                    "Archival manuscripts and unpublished material.",
                    ["title", "author"],
                    ["issued", "submitted", "genre", "archive", "archive-place", "archive_location"],
                    [],
                    dateRoles: [ItemDateRoles.Issued, ItemDateRoles.Submitted, ItemDateRoles.Accessed],
                    identifierSchemes: [BuiltInIdentifierSchemes.CallNumber, BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["call-number"] = "档案号"
                    }),
                Create(
                    "map",
                    "Maps and cartographic material.",
                    ["title", "author"],
                    ["issued", "publisher", "publisher-place", "scale", "medium", "dimensions"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "motion_picture",
                    "Films and videos.",
                    ["title"],
                    ["issued", "publisher", "publisher-place", "genre", "medium", "dimensions"],
                    [],
                    [ItemCreatorRoles.Director, ItemCreatorRoles.Producer, ItemCreatorRoles.ScriptWriter],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "musical_score",
                    "Published musical scores.",
                    ["title"],
                    ["issued", "publisher", "publisher-place", "edition", "medium"],
                    [],
                    [ItemCreatorRoles.Composer],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.ISBN, BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL]),
                Create(
                    "pamphlet",
                    "Pamphlets and booklets.",
                    ["title", "author"],
                    ["issued", "publisher", "publisher-place", "genre", "medium"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "paper-conference",
                    "Conference proceedings papers.",
                    ["title", "author", "container-title"],
                    ["event-title", "event-place", "issued", "publisher", "publisher-place", "pages"],
                    [],
                    identifierSchemes:
                    [
                        BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.ISBN,
                        BuiltInIdentifierSchemes.URL
                    ],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["container-title"] = "会议录名"
                    }),
                Create(
                    "patent",
                    "Patent records.",
                    ["title"],
                    ["authority", "jurisdiction", "number", "issued", "submitted", "status", "references"],
                    [],
                    dateRoles: [ItemDateRoles.Issued, ItemDateRoles.Submitted],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL, BuiltInIdentifierSchemes.CallNumber],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["call-number"] = "专利号",
                        ["number"] = "专利号",
                        ["publisher"] = "专利局/机构"
                    }),
                Create(
                    "performance",
                    "Live performances.",
                    ["title"],
                    ["event-title", "event-place", "event-date", "genre", "medium"],
                    [],
                    [ItemCreatorRoles.Performer, ItemCreatorRoles.Composer, ItemCreatorRoles.Author],
                    [ItemDateRoles.EventDate],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "periodical",
                    "Whole journal or magazine issues.",
                    ["title"],
                    ["issued", "volume", "issue", "publisher", "publisher-place"],
                    [],
                    [ItemCreatorRoles.Editor],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.ISSN, BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["title"] = "期刊名",
                        ["container-title"] = "期刊名"
                    }),
                Create(
                    "personal_communication",
                    "Letters, emails and other personal communications.",
                    ["title", "author"],
                    ["issued", "genre", "medium", "archive", "archive_location"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.Recipient],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "post",
                    "Forum and social-media posts.",
                    ["title", "author", "container-title"],
                    ["issued", "accessed", "genre"],
                    [],
                    dateRoles: [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "post-weblog",
                    "Blog posts.",
                    ["title", "author", "container-title"],
                    ["issued", "accessed"],
                    [],
                    dateRoles: [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["container-title"] = "博客名"
                    }),
                Create(
                    "regulation",
                    "Administrative regulations.",
                    ["title"],
                    ["authority", "jurisdiction", "number", "section", "issued", "status", "references"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "report",
                    "Institutional or technical reports.",
                    ["title", "author"],
                    ["issued", "publisher", "publisher-place", "genre", "number", "collection-title"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["number"] = "编号"
                    }),
                Create(
                    "review",
                    "Reviews of works.",
                    ["title", "author"],
                    ["reviewed-title", "reviewed-genre", "container-title", "issued", "volume", "issue", "pages"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.ReviewedAuthor],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "review-book",
                    "Book reviews.",
                    ["title", "author"],
                    ["reviewed-title", "container-title", "issued", "volume", "issue", "pages"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.ReviewedAuthor],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "software",
                    "Computer software.",
                    ["title"],
                    ["issued", "version", "publisher", "genre", "medium", "license"],
                    [],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.OriginalAuthor],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL]),
                Create(
                    "song",
                    "Songs and audio tracks.",
                    ["title"],
                    ["container-title", "issued", "publisher", "medium", "genre"],
                    [],
                    [ItemCreatorRoles.Composer, ItemCreatorRoles.Performer],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    [BuiltInIdentifierSchemes.URL]),
                Create(
                    "speech",
                    "Speeches and presentations.",
                    ["title", "author"],
                    ["event-title", "event-place", "event-date", "issued", "genre", "container-title"],
                    [],
                    dateRoles: [ItemDateRoles.EventDate, ItemDateRoles.Issued],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "standard",
                    "Standards and specifications.",
                    ["title"],
                    ["authority", "number", "version", "issued", "publisher", "publisher-place", "status"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["number"] = "编号"
                    }),
                Create(
                    "thesis",
                    "Dissertations and theses.",
                    ["title", "author"],
                    ["issued", "genre", "publisher", "archive"],
                    [],
                    identifierSchemes: [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["publisher"] = "授予机构"
                    }),
                Create(
                    "treaty",
                    "Treaties between states or organisations.",
                    ["title"],
                    ["authority", "jurisdiction", "issued", "event-date", "event-place", "number", "references"],
                    [],
                    dateRoles: [ItemDateRoles.Issued, ItemDateRoles.EventDate],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create(
                    "webpage",
                    "Online web pages.",
                    ["title"],
                    ["container-title", "issued", "publisher", "accessed"],
                    [],
                    dateRoles: [ItemDateRoles.Issued, ItemDateRoles.Accessed],
                    identifierSchemes: [BuiltInIdentifierSchemes.URL])
            }
            .ToDictionary(profile => profile.ItemType, StringComparer.Ordinal);

    public Task<Result<IReadOnlyList<CslItemTypeProfile>>> ListProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<IReadOnlyList<CslItemTypeProfile>>.Success(Profiles.Values
            .OrderBy(profile => profile.DisplayName, StringComparer.Ordinal).ToArray()));
    }

    public Task<Result<CslItemTypeProfile>> GetProfileAsync(string itemType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemType))
        {
            return Task.FromResult(
                Result<CslItemTypeProfile>.Failure(AppErrorCodes.ValidationFailed, "Item type is required."));
        }

        return Task.FromResult(
            Profiles.TryGetValue(itemType.Trim(), out CslItemTypeProfile? profile)
                ? Result<CslItemTypeProfile>.Success(profile)
                : Result<CslItemTypeProfile>.Failure(AppErrorCodes.NotFound, "Item type profile was not found."));
    }

    public async Task<Result> ValidateItemTypeAsync(string itemType, CancellationToken cancellationToken = default)
    {
        Result<CslItemTypeProfile> profile = await GetProfileAsync(itemType, cancellationToken);
        return profile.IsSuccess
            ? Result.Success()
            : Result.Failure(profile.ErrorCode!, profile.ErrorMessage!);
    }

    private static CslItemTypeProfile Create(
        string itemType,
        string description,
        IReadOnlyList<string> primaryFields,
        IReadOnlyList<string> recommendedFields,
        IReadOnlyList<string> advancedFields,
        IReadOnlyList<string>? creatorRoles = null,
        IReadOnlyList<string>? dateRoles = null,
        IReadOnlyList<string>? identifierSchemes = null,
        IReadOnlyList<string>? hiddenByDefaultFields = null,
        IReadOnlyDictionary<string, string>? fieldLabels = null,
        bool renderableInCsl = true)
    {
        Dictionary<string, string> labels = new(StringComparer.Ordinal)
        {
            ["title"] = "标题",
            ["author"] = "作者/贡献者",
            ["issued"] = "日期",
            ["publisher"] = "出版社/机构",
            ["container-title"] = "文献出处",
            ["pages"] = "页码"
        };

        if (fieldLabels is not null)
        {
            foreach ((string field, string label) in fieldLabels)
            {
                labels[field] = label;
            }
        }

        return new CslItemTypeProfile(
            itemType,
            CslItemTypeDisplayNames.For(itemType),
            description,
            primaryFields,
            recommendedFields,
            advancedFields,
            creatorRoles ?? [ItemCreatorRoles.Author],
            dateRoles ?? [ItemDateRoles.Issued],
            identifierSchemes ?? [],
            labels,
            hiddenByDefaultFields ?? [],
            renderableInCsl);
    }
}
