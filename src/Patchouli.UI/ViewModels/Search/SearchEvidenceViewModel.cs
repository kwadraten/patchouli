using System.Collections.ObjectModel;
using System.Text.Json;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.UI.Services;

namespace Patchouli.UI.ViewModels;

public sealed class SearchEvidenceViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    public string DocumentInstanceId { get; set; } = "";
    public string Query { get; set; } = "";
    public string UnitId { get; set; } = "";
    public string VersionedUri { get; set; } = "";
    public string Markdown { get; set; } = "";
    public string Output { get; set; } = "";
    public string IndexStatus { get; private set; } = "";
    public string AffectedScopesSummary { get; private set; } = "";
    public string EstimatedTotalText { get; private set; } = "";
    public ObservableCollection<string> SearchUnits { get; } = new();
    public ObservableCollection<SearchPageResultViewModel> Results { get; } = new();
    public bool HasResults => Results.Count > 0;
    public bool HasNoResults => !HasResults && !string.IsNullOrWhiteSpace(Query);
    public AsyncCommand RebuildCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand MarkdownCommand { get; }
    public AsyncCommand CopyMarkdownCommand { get; }

    public SearchEvidenceViewModel(MainWindowViewModel m)
    {
        _main = m;
        RebuildCommand = new AsyncCommand(async () =>
        {
            AppServices s = await _main.ServicesAsync();
            Result a = await s.SearchUnits.RebuildForDocumentInstanceAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));
            Result b = await s.SearchIndex.RebuildFtsForDocumentInstanceAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));
            Output = a.IsSuccess && b.IsSuccess ? "搜索单元和 FTS 已重建。" : $"ERROR {a.ErrorCode ?? b.ErrorCode}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("rebuild_search_fts", Output);
        });
        SearchCommand = new AsyncCommand(SearchAsync);
        MarkdownCommand = new AsyncCommand(async () =>
        {
            Result<EvidencePageText> r = await ResolveMarkdownAsync(VersionedUri);
            Markdown = r.IsSuccess ? r.Value.Markdown : "";
            Output = r.IsSuccess ? Markdown : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Markdown));
            Raise(nameof(Output));
        });
        CopyMarkdownCommand = new AsyncCommand(async () =>
        {
            if (string.IsNullOrWhiteSpace(Markdown))
            {
                Output = "ERROR validation_failed: 请先生成证据 Markdown。";
            }
            else
            {
                try
                {
                    await _main.Clipboard.SetTextAsync(Markdown);
                    Output = "Copied Evidence Markdown";
                }
                catch (Exception ex)
                {
                    Output = $"ERROR clipboard_unavailable: {ex.Message}";
                }
            }

            Raise(nameof(Output));
            await _main.LogOperationAsync("copy_evidence_markdown", Output);
        });
    }

    public async Task CopyVersionedUriAsync(string? versionedUri)
    {
        if (string.IsNullOrWhiteSpace(versionedUri))
        {
            Output = "ERROR validation_failed: 缺少版本化证据 URI。";
            Raise(nameof(Output));
            await _main.LogOperationAsync("copy_evidence_uri", Output);
            return;
        }

        try
        {
            await _main.Clipboard.SetTextAsync(versionedUri);
            VersionedUri = versionedUri;
            Output = "Copied Evidence URI";
            Raise(nameof(VersionedUri));
        }
        catch (Exception ex)
        {
            Output = $"ERROR clipboard_unavailable: {ex.Message}";
        }

        Raise(nameof(Output));
        await _main.LogOperationAsync("copy_evidence_uri", Output);
    }

    public async Task CopyVersionedUriForSearchUnitAsync(SearchMatchedUnitViewModel unit)
    {
        string uri = unit.VersionedUri;
        string text = $"{uri}\n\n> {unit.Text}";
        await CopyVersionedUriAsync(text);
    }

    public async Task CopyEvidenceMarkdownAsync(string? versionedUri)
    {
        if (string.IsNullOrWhiteSpace(versionedUri))
        {
            Output = "ERROR validation_failed: 缺少版本化证据 URI。";
            Raise(nameof(Output));
            await _main.LogOperationAsync("copy_search_result_evidence_markdown", Output);
            return;
        }

        Result<EvidencePageText> markdown = await ResolveMarkdownAsync(versionedUri);
        if (markdown.IsFailure)
        {
            Output = $"ERROR {markdown.ErrorCode}: {markdown.ErrorMessage}";
            Raise(nameof(Output));
            _main.Report(Output);
            await _main.LogOperationAsync("copy_search_result_evidence_markdown", Output);
            return;
        }

        try
        {
            await _main.Clipboard.SetTextAsync(markdown.Value.Markdown);
            VersionedUri = versionedUri;
            Markdown = markdown.Value.Markdown;
            Output = "Copied Evidence Markdown";
            Raise(nameof(VersionedUri));
            Raise(nameof(Markdown));
            _main.Report("已复制证据 Markdown。");
        }
        catch (Exception ex)
        {
            Output = $"ERROR clipboard_unavailable: {ex.Message}";
        }

        Raise(nameof(Output));
        await _main.LogOperationAsync("copy_search_result_evidence_markdown", Output);
    }

    public async Task CopyEvidenceMarkdownForSearchUnitAsync(SearchMatchedUnitViewModel unit)
    {
        await CopyEvidenceMarkdownAsync(unit.VersionedUri);
    }

    public string BuildVersionedUri(SearchMatchedUnitViewModel unit)
    {
        return unit.VersionedUri;
    }

    public void RaiseMarkdown()
    {
        Raise(nameof(Markdown));
    }

    public void RaiseOutput()
    {
        Raise(nameof(Output));
    }

    private async Task<Result<EvidencePageText>> ResolveMarkdownAsync(string? versionedUri)
    {
        if (string.IsNullOrWhiteSpace(versionedUri))
        {
            return Result<EvidencePageText>.Failure(AppErrorCodes.ValidationFailed, "缺少版本化证据 URI。");
        }

        PatchouliNavigationParseResult parsed = PatchouliUriNavigationParser.ParseInput(versionedUri);
        if (!parsed.IsSuccess || parsed.Target is not { Kind: PatchouliNavigationKind.TextPage } target)
        {
            return Result<EvidencePageText>.Failure(AppErrorCodes.ValidationFailed, "无法解析版本化证据 URI。");
        }

        AppServices services = await _main.ServicesAsync();
        return await services.VersionedEvidenceReader.GetBoxTextAsync(
            Patchouli.Core.Ids.DocumentInstanceId.Parse(target.ResourceId),
            (target.PageIndex ?? 0) + 1,
            target.RevisionId,
            target.BoxId);
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            Results.Clear();
            SearchUnits.Clear();
            IndexStatus = "";
            AffectedScopesSummary = "";
            EstimatedTotalText = "";
            Output = "";
            Raise(nameof(IndexStatus));
            Raise(nameof(AffectedScopesSummary));
            Raise(nameof(EstimatedTotalText));
            Raise(nameof(Results));
            Raise(nameof(HasResults));
            Raise(nameof(HasNoResults));
            Raise(nameof(Output));
            _main.Report("请输入搜索词。");
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result<SearchResultPage> r = await services.Search.SearchLibraryAsync(new SearchRequest(Query));
        Results.Clear();
        SearchUnits.Clear();

        if (r.IsSuccess)
        {
            string? firstMatchedUnit = default;
            foreach (SearchPageResult page in r.Value.Results)
            {
                List<SearchMatchedUnitViewModel> matchedUnits = new();
                foreach (SearchMatchedUnit unit in page.MatchedUnits)
                {
                    SearchUnits.Add($"{unit.UnitId} | {unit.Text}");
                    matchedUnits.Add(new SearchMatchedUnitViewModel(
                        unit.UnitId.ToString(),
                        unit.Text,
                        unit.BoxType,
                        unit.Ordinal,
                        unit.IsMatch,
                        page.DocumentInstanceId,
                        page.PageIndex,
                        unit.BoxId,
                        unit.TreeRevisionId));
                    firstMatchedUnit ??= unit.UnitId.ToString();
                }

                Results.Add(new SearchPageResultViewModel(
                    page.ItemTitle,
                    page.DocumentInstanceId.ToString(),
                    page.PageId.ToString(),
                    page.PageLabel,
                    page.PageIndex,
                    page.IndexStatus,
                    page.MatchedUnitsHasMore,
                    matchedUnits));
            }

            UnitId = firstMatchedUnit ?? "";
            VersionedUri = "";
            IndexStatus = r.Value.IndexStatus;
            AffectedScopesSummary = r.Value.AffectedScopesSummary ?? "";
            EstimatedTotalText = r.Value.EstimatedTotal?.ToString() ?? $"{r.Value.Results.Count} 页";
            Output = JsonSerializer.Serialize(r.Value, new JsonSerializerOptions { WriteIndented = true });
            _main.Report(r.Value.Results.Count > 0
                ? $"搜索完成：{r.Value.Results.Count} 页命中，索引状态={IndexStatus}。"
                : $"搜索完成：没有命中结果，索引状态={IndexStatus}。");
        }
        else
        {
            UnitId = "";
            VersionedUri = "";
            IndexStatus = "";
            AffectedScopesSummary = "";
            EstimatedTotalText = "";
            Output = $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            _main.Report(Output);
        }

        Raise(nameof(UnitId));
        Raise(nameof(VersionedUri));
        Raise(nameof(IndexStatus));
        Raise(nameof(AffectedScopesSummary));
        Raise(nameof(EstimatedTotalText));
        Raise(nameof(Results));
        Raise(nameof(HasResults));
        Raise(nameof(HasNoResults));
        Raise(nameof(Output));
    }
}
