using System.Collections.ObjectModel;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Search;

namespace Patchouli.UI.ViewModels;

public sealed class SearchProfileViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public SearchProfileViewModel(MainWindowViewModel main)
    {
        _main = main;
        CreateProfileCommand = new AsyncCommand(CreateAsync);
        AddRuleCommand = new AsyncCommand(AddRuleAsync);
        PreviewCommand = new AsyncCommand(PreviewAsync);
        SearchCommand = new AsyncCommand(SearchAsync);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        SetDefaultCommand = new AsyncCommand(SetDefaultAsync);
    }

    public string Name { get; set; } = "Research variants";
    public string Description { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string RuleType { get; set; } = SearchRuleType.Variant;
    public string Pattern { get; set; } = "";
    public string Replacement { get; set; } = "";
    public string Direction { get; set; } = SearchRewriteDirection.Bidirectional;
    public string Priority { get; set; } = "0";
    public string Query { get; set; } = "";

    public string Output { get; private set; } =
        "Canonical text is unchanged. Rewrites affect the query only; expansions have equal weight.";

    public ObservableCollection<string> Profiles { get; } = new();
    public AsyncCommand CreateProfileCommand { get; }
    public AsyncCommand AddRuleCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SetDefaultCommand { get; }

    private async Task CreateAsync()
    {
        Result<SearchProfile> r =
            await (await _main.ServicesAsync()).SearchProfiles.CreateProfileAsync(Name, Description);
        if (r.IsSuccess)
        {
            ProfileId = r.Value.ProfileId.ToString();
            Output = $"Profile: {ProfileId}";
            Raise(nameof(ProfileId));
        }
        else
        {
            Output = $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
        }

        Raise(nameof(Output));
        await RefreshAsync();
    }

    private async Task AddRuleAsync()
    {
        try
        {
            Result<SearchRewriteRule> r = await (await _main.ServicesAsync()).SearchProfiles.AddRewriteRuleAsync(
                string.IsNullOrWhiteSpace(ProfileId) ? null : SearchProfileId.Parse(ProfileId), RuleType, Pattern,
                Replacement, Direction, int.Parse(Priority), null);
            Output = r.IsSuccess ? $"Rule: {r.Value.RuleId}" : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
        }
        catch (Exception ex)
        {
            Output = $"ERROR validation_failed: {ex.Message}";
        }

        Raise(nameof(Output));
    }

    private async Task PreviewAsync()
    {
        try
        {
            AppServices services = await _main.ServicesAsync();
            Result<SearchRewritePlan> r = await services.QueryRewriter.BuildRewritePlanAsync(Query,
                new SearchRewriteOptions(LibraryId.New(),
                    string.IsNullOrWhiteSpace(ProfileId) ? null : SearchProfileId.Parse(ProfileId), PreviewOnly: true));
            Output = r.IsSuccess
                ? System.Text.Json.JsonSerializer.Serialize(r.Value,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    })
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
        }
        catch (Exception ex)
        {
            Output = $"ERROR validation_failed: {ex.Message}";
        }

        Raise(nameof(Output));
    }

    private async Task SearchAsync()
    {
        try
        {
            Result<SearchResultPage> r = await (await _main.ServicesAsync()).Search.SearchLibraryAsync(
                new SearchRequest(Query,
                    ProfileId: string.IsNullOrWhiteSpace(ProfileId) ? null : SearchProfileId.Parse(ProfileId)));
            Output = r.IsSuccess
                ? System.Text.Json.JsonSerializer.Serialize(r.Value,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
        }
        catch (Exception ex)
        {
            Output = $"ERROR validation_failed: {ex.Message}";
        }

        Raise(nameof(Output));
    }

    private async Task RefreshAsync()
    {
        Result<IReadOnlyList<SearchProfile>> r = await (await _main.ServicesAsync()).SearchProfiles.ListProfilesAsync();
        Profiles.Clear();
        if (r.IsSuccess)
        {
            foreach (SearchProfile p in r.Value)
            {
                Profiles.Add($"{p.ProfileId} | {p.Name}{(p.IsDefault ? " | default" : "")}");
            }
        }

        Raise(nameof(Profiles));
    }

    private async Task SetDefaultAsync()
    {
        try
        {
            Result r =
                await (await _main.ServicesAsync()).SearchProfiles.SetDefaultProfileAsync(
                    SearchProfileId.Parse(ProfileId));
            Output = r.IsSuccess ? "Default profile updated." : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
        }
        catch (Exception ex)
        {
            Output = $"ERROR validation_failed: {ex.Message}";
        }

        Raise(nameof(Output));
        await RefreshAsync();
    }
}
