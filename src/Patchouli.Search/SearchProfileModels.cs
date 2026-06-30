using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Search;

public static class SearchRuleType
{
    public const string Literal = "literal";
    public const string Variant = "variant";
    public const string SimplifiedTraditional = "simplified_traditional";
    public const string OldNewForm = "old_new_form";
    public const string OcrConfusion = "ocr_confusion";
    public const string Synonym = "synonym";
    public const string Regex = "regex";
    public const string CommandAlias = "command_alias";
    public const string HistoricalKana = "historical_kana";
}

public static class SearchRewriteDirection { public const string Expand = "expand"; public const string Replace = "replace"; public const string Bidirectional = "bidirectional"; }

public sealed record SearchProfile(SearchProfileId ProfileId, LibraryId LibraryId, string Name, string? Description, bool IsSystem, bool IsDefault, bool Archived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SearchRewriteRule(SearchRewriteRuleId RuleId, LibraryId LibraryId, SearchProfileId? ProfileId, string RuleType, string Pattern, string Replacement, string Direction, bool Enabled, int Priority, string? Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SearchProfileSettings(LibraryId LibraryId, SearchProfileId? DefaultProfileId, SearchProfileId? LastUsedProfileId, bool PreviewBeforeExecute, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SearchRewriteExpansion(string OriginalTerm, IReadOnlyList<string> ExpandedTerms, IReadOnlyList<SearchRewriteRuleId> RuleIds, IReadOnlyList<string> RuleTypes);
public sealed record SearchRewritePlan(string OriginalQuery, SearchProfileId? EffectiveProfileId, string? EffectiveProfileName, IReadOnlyList<string> ExpandedQueries, IReadOnlyList<SearchRewriteRuleId> AppliedRules, IReadOnlyList<string> Warnings, bool PreviewOnly);
public sealed record SearchRewriteOptions(LibraryId LibraryId, SearchProfileId? ExplicitProfileId = null, SearchProfileId? SelectedProfileId = null, string? Alias = null, bool PreviewOnly = false, int MaxExpansions = 32, bool EnableRegex = true);

public interface ISearchProfileService
{
    Task<Result<SearchProfile>> CreateProfileAsync(string name, string? description, CancellationToken cancellationToken = default);
    Task<Result<SearchProfile>> UpdateProfileAsync(SearchProfileId profileId, string name, string? description, bool archived, CancellationToken cancellationToken = default);
    Task<Result> SetDefaultProfileAsync(SearchProfileId profileId, CancellationToken cancellationToken = default);
    Task<Result<SearchProfile>> GetDefaultProfileAsync(CancellationToken cancellationToken = default);
    Task<Result> SetLastUsedProfileAsync(SearchProfileId profileId, CancellationToken cancellationToken = default);
    Task<Result<SearchProfile>> GetEffectiveProfileAsync(SearchProfileId? explicitProfileId, string? alias, SearchProfileId? selectedProfileId, CancellationToken cancellationToken = default);
    Task<Result<SearchRewriteRule>> AddRewriteRuleAsync(SearchProfileId? profileId, string ruleType, string pattern, string replacement, string direction, int priority, string? note, CancellationToken cancellationToken = default);
    Task<Result> EnableRuleAsync(SearchRewriteRuleId ruleId, CancellationToken cancellationToken = default);
    Task<Result> DisableRuleAsync(SearchRewriteRuleId ruleId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<SearchProfile>>> ListProfilesAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<SearchRewriteRule>>> ListRulesAsync(SearchProfileId? profileId = null, bool includeDisabled = false, CancellationToken cancellationToken = default);
    Task<Result> DeleteRuleAsync(SearchRewriteRuleId ruleId, CancellationToken cancellationToken = default);
}

public interface IQueryRewriter
{
    Task<Result<SearchRewritePlan>> BuildRewritePlanAsync(string query, SearchRewriteOptions options, CancellationToken cancellationToken = default);
}
