using System.Text.RegularExpressions;
using Dapper;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Search;

namespace Patchouli.Infrastructure.Search;

public sealed class SearchProfileService : ISearchProfileService, IQueryRewriter
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _library;
    private readonly IClock _clock;

    static SearchProfileService() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public SearchProfileService(SqliteConnectionFactory connectionFactory, ILibraryIdentityService library, IClock clock)
    {
        _connectionFactory = connectionFactory; _library = library; _clock = clock;
    }

    public async Task<Result<SearchProfile>> CreateProfileAsync(string name, string? description, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result<SearchProfile>.Failure(AppErrorCodes.ValidationFailed, "Profile name is required.");
        var library = await CurrentLibraryAsync(cancellationToken); if (library.IsFailure) return Result<SearchProfile>.Failure(library.ErrorCode!, library.ErrorMessage!);
        try
        {
            var profile = new SearchProfile(SearchProfileId.New(), library.Value, name.Trim(), description, false, false, false, _clock.UtcNow, _clock.UtcNow);
            await using var connection = _connectionFactory.CreateConnection(); await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("insert into search_profiles(profile_id,library_id,name,description,is_system,is_default,archived,created_at,updated_at) values(@Id,@Library,@Name,@Description,0,0,0,@Now,@Now);", new { Id = profile.ProfileId.ToString(), Library = profile.LibraryId.ToString(), profile.Name, profile.Description, Now = _clock.UtcNow.ToString("O") });
            return Result<SearchProfile>.Success(profile);
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-profile")) { return Result<SearchProfile>.Failure(AppErrorCodes.DatabaseError, ex.Message); }
    }

    public async Task<Result<SearchProfile>> UpdateProfileAsync(SearchProfileId profileId, string name, string? description, bool archived, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result<SearchProfile>.Failure(AppErrorCodes.ValidationFailed, "Profile name is required.");
        var existing = await GetProfileAsync(profileId, cancellationToken); if (existing.IsFailure) return existing;
        try { await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(cancellationToken); await c.ExecuteAsync("update search_profiles set name=@Name,description=@Description,archived=@Archived,updated_at=@Now where profile_id=@Id;", new { Id = profileId.ToString(), Name = name.Trim(), Description = description, Archived = archived ? 1 : 0, Now = _clock.UtcNow.ToString("O") }); return Result<SearchProfile>.Success(existing.Value with { Name = name.Trim(), Description = description, Archived = archived, UpdatedAt = _clock.UtcNow }); }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-profile")) { return Result<SearchProfile>.Failure(AppErrorCodes.DatabaseError, ex.Message); }
    }

    public async Task<Result> SetDefaultProfileAsync(SearchProfileId profileId, CancellationToken cancellationToken = default)
    {
        var profile = await GetProfileAsync(profileId, cancellationToken); if (profile.IsFailure) return Result.Failure(profile.ErrorCode!, profile.ErrorMessage!);
        try { await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(cancellationToken); await using var tx = await c.BeginTransactionAsync(cancellationToken); var now = _clock.UtcNow.ToString("O"); await c.ExecuteAsync("update search_profiles set is_default=0 where library_id=@Library;", new { Library = profile.Value.LibraryId.ToString() }, tx); await c.ExecuteAsync("update search_profiles set is_default=1 where profile_id=@Id;", new { Id = profileId.ToString() }, tx); await UpsertSettingsAsync(c, profile.Value.LibraryId, profileId, null, tx); await tx.CommitAsync(cancellationToken); return Result.Success(); }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-profile")) { return Result.Failure(AppErrorCodes.DatabaseError, ex.Message); }
    }

    public async Task<Result<SearchProfile>> GetDefaultProfileAsync(CancellationToken cancellationToken = default)
    {
        var library = await CurrentLibraryAsync(cancellationToken); if (library.IsFailure) return Result<SearchProfile>.Failure(library.ErrorCode!, library.ErrorMessage!);
        try
        {
            await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(cancellationToken);
            var row = await c.QuerySingleOrDefaultAsync<Row>("select * from search_profiles where library_id=@Library and is_default=1 and archived=0 limit 1;", new { Library = library.Value.ToString() });
            if (row is not null) return Result<SearchProfile>.Success(row.ToProfile());
            var profile = new SearchProfile(SearchProfileId.New(), library.Value, "System Default", "Minimal auditable query rewrite profile.", true, true, false, _clock.UtcNow, _clock.UtcNow);
            await c.ExecuteAsync("insert into search_profiles(profile_id,library_id,name,description,is_system,is_default,archived,created_at,updated_at) values(@Id,@Library,@Name,@Description,1,1,0,@Now,@Now);", new { Id = profile.ProfileId.ToString(), Library = profile.LibraryId.ToString(), profile.Name, profile.Description, Now = _clock.UtcNow.ToString("O") });
            await UpsertSettingsAsync(c, library.Value, profile.ProfileId, null);
            return Result<SearchProfile>.Success(profile);
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-profile")) { return Result<SearchProfile>.Failure(AppErrorCodes.DatabaseError, ex.Message); }
    }

    public async Task<Result> SetLastUsedProfileAsync(SearchProfileId profileId, CancellationToken cancellationToken = default)
    {
        var profile = await GetProfileAsync(profileId, cancellationToken); if (profile.IsFailure) return Result.Failure(profile.ErrorCode!, profile.ErrorMessage!);
        try { await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(cancellationToken); await UpsertSettingsAsync(c, profile.Value.LibraryId, null, profileId); return Result.Success(); }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-profile")) { return Result.Failure(AppErrorCodes.DatabaseError, ex.Message); }
    }

    public async Task<Result<SearchProfile>> GetEffectiveProfileAsync(SearchProfileId? explicitProfileId, string? alias, SearchProfileId? selectedProfileId, CancellationToken cancellationToken = default)
    {
        if (explicitProfileId is not null) return await GetProfileAsync(explicitProfileId.Value, cancellationToken);
        var library = await CurrentLibraryAsync(cancellationToken); if (library.IsFailure) return Result<SearchProfile>.Failure(library.ErrorCode!, library.ErrorMessage!);
        await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(alias))
        {
            var aliasName = alias.Trim().TrimStart('@');
            var row = await c.QuerySingleOrDefaultAsync<Row>("select * from search_profiles where library_id=@Library and lower(name)=lower(@Name) and archived=0 limit 1;", new { Library = library.Value.ToString(), Name = aliasName });
            if (row is not null) return Result<SearchProfile>.Success(row.ToProfile());
        }
        if (selectedProfileId is not null) return await GetProfileAsync(selectedProfileId.Value, cancellationToken);
        var last = await c.ExecuteScalarAsync<string?>("select last_used_profile_id from search_settings where library_id=@Library;", new { Library = library.Value.ToString() });
        if (last is not null) return await GetProfileAsync(SearchProfileId.Parse(last), cancellationToken);
        return await GetDefaultProfileAsync(cancellationToken);
    }

    public async Task<Result<SearchRewriteRule>> AddRewriteRuleAsync(SearchProfileId? profileId, string ruleType, string pattern, string replacement, string direction, int priority, string? note, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleType) || string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(replacement) || string.IsNullOrWhiteSpace(direction)) return Result<SearchRewriteRule>.Failure(AppErrorCodes.ValidationFailed, "Rule type, pattern, replacement, and direction are required.");
        var library = await CurrentLibraryAsync(cancellationToken); if (library.IsFailure) return Result<SearchRewriteRule>.Failure(library.ErrorCode!, library.ErrorMessage!);
        if (profileId is not null) { var profile = await GetProfileAsync(profileId.Value, cancellationToken); if (profile.IsFailure) return Result<SearchRewriteRule>.Failure(profile.ErrorCode!, profile.ErrorMessage!); }
        try { var rule = new SearchRewriteRule(SearchRewriteRuleId.New(), library.Value, profileId, ruleType, pattern, replacement, direction, true, priority, note, _clock.UtcNow, _clock.UtcNow); await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(cancellationToken); await c.ExecuteAsync("insert into search_rewrite_rules(rule_id,library_id,profile_id,rule_type,pattern,replacement,direction,enabled,priority,note,created_at,updated_at) values(@Id,@Library,@Profile,@Type,@Pattern,@Replacement,@Direction,1,@Priority,@Note,@Now,@Now);", new { Id = rule.RuleId.ToString(), Library = rule.LibraryId.ToString(), Profile = rule.ProfileId?.ToString(), Type = rule.RuleType, rule.Pattern, rule.Replacement, rule.Direction, rule.Priority, rule.Note, Now = _clock.UtcNow.ToString("O") }); return Result<SearchRewriteRule>.Success(rule); }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-profile")) { return Result<SearchRewriteRule>.Failure(AppErrorCodes.DatabaseError, ex.Message); }
    }

    public Task<Result> EnableRuleAsync(SearchRewriteRuleId ruleId, CancellationToken cancellationToken = default) => SetRuleEnabledAsync(ruleId, true, cancellationToken);
    public Task<Result> DisableRuleAsync(SearchRewriteRuleId ruleId, CancellationToken cancellationToken = default) => SetRuleEnabledAsync(ruleId, false, cancellationToken);
    public async Task<Result<IReadOnlyList<SearchProfile>>> ListProfilesAsync(bool includeArchived = false, CancellationToken cancellationToken = default) { var library = await CurrentLibraryAsync(cancellationToken); if (library.IsFailure) return Result<IReadOnlyList<SearchProfile>>.Failure(library.ErrorCode!, library.ErrorMessage!); await GetDefaultProfileAsync(cancellationToken); await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(cancellationToken); var rows = await c.QueryAsync<Row>("select * from search_profiles where library_id=@Library and (@Archived=1 or archived=0) order by is_default desc,name;", new { Library = library.Value.ToString(), Archived = includeArchived ? 1 : 0 }); return Result<IReadOnlyList<SearchProfile>>.Success(rows.Select(x => x.ToProfile()).ToArray()); }
    public async Task<Result<IReadOnlyList<SearchRewriteRule>>> ListRulesAsync(SearchProfileId? profileId = null, bool includeDisabled = false, CancellationToken cancellationToken = default) { var library = await CurrentLibraryAsync(cancellationToken); if (library.IsFailure) return Result<IReadOnlyList<SearchRewriteRule>>.Failure(library.ErrorCode!, library.ErrorMessage!); await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(cancellationToken); var rows = await c.QueryAsync<RuleRow>("select * from search_rewrite_rules where library_id=@Library and (@Profile is null or profile_id is null or profile_id=@Profile) and (@Disabled=1 or enabled=1) order by priority desc,created_at;", new { Library = library.Value.ToString(), Profile = profileId?.ToString(), Disabled = includeDisabled ? 1 : 0 }); return Result<IReadOnlyList<SearchRewriteRule>>.Success(rows.Select(x => x.ToRule()).ToArray()); }
    public async Task<Result> DeleteRuleAsync(SearchRewriteRuleId ruleId, CancellationToken cancellationToken = default) { var row = await FindRuleAsync(ruleId, cancellationToken); if (row.IsFailure) return Result.Failure(row.ErrorCode!, row.ErrorMessage!); await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(cancellationToken); await c.ExecuteAsync("delete from search_rewrite_rules where rule_id=@Id;", new { Id = ruleId.ToString() }); return Result.Success(); }

    public async Task<Result<SearchRewritePlan>> BuildRewritePlanAsync(string query, SearchRewriteOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Result<SearchRewritePlan>.Failure(AppErrorCodes.ValidationFailed, "Search query is required.");
        var profile = await GetEffectiveProfileAsync(options.ExplicitProfileId, options.Alias, options.SelectedProfileId, cancellationToken); if (profile.IsFailure) return Result<SearchRewritePlan>.Failure(profile.ErrorCode!, profile.ErrorMessage!);
        var rules = await ListRulesAsync(profile.Value.ProfileId, false, cancellationToken); if (rules.IsFailure) return Result<SearchRewritePlan>.Failure(rules.ErrorCode!, rules.ErrorMessage!);
        var expansions = new List<string> { query.Trim() }; var applied = new List<SearchRewriteRuleId>(); var warnings = new List<string>();
        foreach (var rule in rules.Value.Where(r => r.Enabled).OrderByDescending(r => r.Priority))
        {
            if (rule.RuleType == SearchRuleType.Regex && !options.EnableRegex) continue;
            try
            {
                var snapshot = expansions.ToArray();
                foreach (var term in snapshot)
                {
                    foreach (var expanded in ApplyRule(term, rule)) if (!string.IsNullOrWhiteSpace(expanded) && !expansions.Contains(expanded, StringComparer.Ordinal)) { expansions.Add(expanded); applied.Add(rule.RuleId); if (expansions.Count >= Math.Clamp(options.MaxExpansions, 1, 128)) break; }
                    if (expansions.Count >= Math.Clamp(options.MaxExpansions, 1, 128)) break;
                }
            }
            catch (ArgumentException ex) { warnings.Add($"Rule {rule.RuleId} was skipped: {ex.Message}"); }
            if (expansions.Count >= Math.Clamp(options.MaxExpansions, 1, 128)) { warnings.Add("Maximum query expansions reached."); break; }
        }
        return Result<SearchRewritePlan>.Success(new SearchRewritePlan(query.Trim(), profile.Value.ProfileId, profile.Value.Name, expansions, applied.Distinct().ToArray(), warnings, options.PreviewOnly));
    }

    private static IEnumerable<string> ApplyRule(string term, SearchRewriteRule rule)
    {
        if (rule.RuleType == SearchRuleType.Regex) { if (Regex.IsMatch(term, rule.Pattern)) yield return Regex.Replace(term, rule.Pattern, rule.Replacement); yield break; }
        var match = term.Contains(rule.Pattern, StringComparison.Ordinal); if (match) yield return rule.Direction == SearchRewriteDirection.Replace ? term.Replace(rule.Pattern, rule.Replacement, StringComparison.Ordinal) : rule.Replacement;
        if (rule.Direction == SearchRewriteDirection.Bidirectional && term.Contains(rule.Replacement, StringComparison.Ordinal)) yield return rule.Pattern;
        if (rule.RuleType == SearchRuleType.CommandAlias && string.Equals(term, rule.Pattern, StringComparison.OrdinalIgnoreCase)) yield return rule.Replacement;
    }

    private async Task<Result> SetRuleEnabledAsync(SearchRewriteRuleId id, bool enabled, CancellationToken ct) { var rule = await FindRuleAsync(id, ct); if (rule.IsFailure) return Result.Failure(rule.ErrorCode!, rule.ErrorMessage!); await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(ct); await c.ExecuteAsync("update search_rewrite_rules set enabled=@Enabled,updated_at=@Now where rule_id=@Id;", new { Id = id.ToString(), Enabled = enabled ? 1 : 0, Now = _clock.UtcNow.ToString("O") }); return Result.Success(); }
    private async Task<Result<SearchProfile>> GetProfileAsync(SearchProfileId id, CancellationToken ct) { var library = await CurrentLibraryAsync(ct); if (library.IsFailure) return Result<SearchProfile>.Failure(library.ErrorCode!, library.ErrorMessage!); await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(ct); var row = await c.QuerySingleOrDefaultAsync<Row>("select * from search_profiles where profile_id=@Id and library_id=@Library;", new { Id = id.ToString(), Library = library.Value.ToString() }); return row is null ? Result<SearchProfile>.Failure(AppErrorCodes.NotFound, "Search profile was not found.") : Result<SearchProfile>.Success(row.ToProfile()); }
    private async Task<Result<SearchRewriteRule>> FindRuleAsync(SearchRewriteRuleId id, CancellationToken ct) { var library = await CurrentLibraryAsync(ct); if (library.IsFailure) return Result<SearchRewriteRule>.Failure(library.ErrorCode!, library.ErrorMessage!); await using var c = _connectionFactory.CreateConnection(); await c.OpenAsync(ct); var row = await c.QuerySingleOrDefaultAsync<RuleRow>("select * from search_rewrite_rules where rule_id=@Id and library_id=@Library;", new { Id = id.ToString(), Library = library.Value.ToString() }); return row is null ? Result<SearchRewriteRule>.Failure(AppErrorCodes.NotFound, "Search rewrite rule was not found.") : Result<SearchRewriteRule>.Success(row.ToRule()); }
    private async Task<Result<LibraryId>> CurrentLibraryAsync(CancellationToken ct) { var result = await _library.GetCurrentLibraryAsync(ct); return result.IsSuccess ? Result<LibraryId>.Success(result.Value.LibraryId) : Result<LibraryId>.Failure(result.ErrorCode!, result.ErrorMessage!); }
    private async Task UpsertSettingsAsync(Microsoft.Data.Sqlite.SqliteConnection c, LibraryId library, SearchProfileId? defaultId, SearchProfileId? lastId, System.Data.Common.DbTransaction? tx = null) { var now = _clock.UtcNow.ToString("O"); await c.ExecuteAsync("insert into search_settings(library_id,default_profile_id,last_used_profile_id,preview_before_execute,created_at,updated_at) values(@Library,@Default,@Last,0,@Now,@Now) on conflict(library_id) do update set default_profile_id=coalesce(excluded.default_profile_id,search_settings.default_profile_id),last_used_profile_id=coalesce(excluded.last_used_profile_id,search_settings.last_used_profile_id),updated_at=excluded.updated_at;", new { Library = library.ToString(), Default = defaultId?.ToString(), Last = lastId?.ToString(), Now = now }, tx); }
    private sealed class Row { public string ProfileId { get; set; } = ""; public string LibraryId { get; set; } = ""; public string Name { get; set; } = ""; public string? Description { get; set; } public int IsSystem { get; set; } public int IsDefault { get; set; } public int Archived { get; set; } public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; public SearchProfile ToProfile()=>new(SearchProfileId.Parse(ProfileId),Patchouli.Core.Ids.LibraryId.Parse(LibraryId),Name,Description,IsSystem!=0,IsDefault!=0,Archived!=0,DateTimeOffset.Parse(CreatedAt),DateTimeOffset.Parse(UpdatedAt)); }
    private sealed class RuleRow { public string RuleId { get; set; } = ""; public string LibraryId { get; set; } = ""; public string? ProfileId { get; set; } public string RuleType { get; set; } = ""; public string Pattern { get; set; } = ""; public string Replacement { get; set; } = ""; public string Direction { get; set; } = ""; public int Enabled { get; set; } public int Priority { get; set; } public string? Note { get; set; } public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; public SearchRewriteRule ToRule()=>new(SearchRewriteRuleId.Parse(RuleId),Patchouli.Core.Ids.LibraryId.Parse(LibraryId),ProfileId is null?null:SearchProfileId.Parse(ProfileId),RuleType,Pattern,Replacement,Direction,Enabled!=0,Priority,Note,DateTimeOffset.Parse(CreatedAt),DateTimeOffset.Parse(UpdatedAt)); }
}
