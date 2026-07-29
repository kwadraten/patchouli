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
                    "General",
                    "Patchouli-specific catch-all type for incomplete or not-yet-classified items.",
                    ["title"],
                    ["author", "issued", "language", "abstract"],
                    ["extra_csl"],
                    [ItemCreatorRoles.Author, ItemCreatorRoles.Editor, ItemCreatorRoles.Translator],
                    [ItemDateRoles.Issued, ItemDateRoles.Accessed, ItemDateRoles.OriginalDate],
                    [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.ISBN, BuiltInIdentifierSchemes.ISSN],
                    ["container-title-short", "call-number", "archive"],
                    renderableInCsl: false),
                Create("book", "Book", "Monographs and books.", ["title", "author"], ["publisher", "issued"],
                    ["collection-title", "extra_csl"], identifierSchemes: [BuiltInIdentifierSchemes.ISBN]),
                Create(
                    "article-journal",
                    "Journal Article",
                    "Articles published in journals.",
                    ["title", "author", "container-title"],
                    ["issued", "volume", "issue", "pages"],
                    ["extra_csl"],
                    identifierSchemes: [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.ISSN],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["container-title"] = "期刊名"
                    }),
                Create(
                    "chapter",
                    "Book Chapter",
                    "Chapters within edited volumes.",
                    ["title", "author", "container-title"],
                    ["publisher", "issued", "pages"],
                    ["extra_csl"],
                    identifierSchemes: [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.ISBN],
                    fieldLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["container-title"] = "文献出处"
                    }),
                Create("thesis", "Thesis", "Dissertations and theses.", ["title", "author"], ["publisher", "issued"],
                    ["genre", "extra_csl"], identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create("report", "Report", "Institutional or technical reports.", ["title", "author"],
                    ["publisher", "issued", "number"], ["extra_csl"],
                    identifierSchemes: [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.URL]),
                Create("webpage", "Web Page", "Online web pages.", ["title"], ["author", "issued", "accessed"],
                    ["extra_csl"], identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create("manuscript", "Manuscript", "Archival manuscripts and unpublished material.", ["title"],
                    ["author", "issued"], ["extra_csl"],
                    identifierSchemes: [BuiltInIdentifierSchemes.ArchiveId, BuiltInIdentifierSchemes.CallNumber]),
                Create("paper-conference", "Conference Paper", "Conference proceedings papers.",
                    ["title", "author", "container-title"], ["issued", "publisher", "pages"], ["extra_csl"],
                    identifierSchemes: [BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.ISBN]),
                Create("patent", "Patent", "Patent records.", ["title"], ["author", "issued", "number"],
                    ["extra_csl"], identifierSchemes: [BuiltInIdentifierSchemes.URL]),
                Create("standard", "Standard", "Standards and specifications.", ["title"],
                    ["publisher", "issued", "number"], ["version", "extra_csl"],
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
        string displayName,
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
            ["pages"] = "页码",
            ["extra_csl"] = "更多 CSL 字段"
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
            displayName,
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
