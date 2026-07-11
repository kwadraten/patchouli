using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using Dapper;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.UI.ViewModels;

public sealed class SearchEvidenceViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    public string DocumentInstanceId { get; set; } = "";
    public string Query { get; set; } = "";
    public string UnitId { get; set; } = "";
    public string EvidenceRef { get; set; } = "";
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
    public AsyncCommand CreateEvidenceCommand { get; }
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
        CreateEvidenceCommand = new AsyncCommand(async () =>
        {
            Result<EvidenceRefRecord> r =
                await (await _main.ServicesAsync()).Evidence.CreateFromSearchUnitAsync(SearchUnitId.Parse(UnitId));
            Output = r.IsSuccess ? r.Value.EvidenceRefId : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            if (r.IsSuccess)
            {
                EvidenceRef = r.Value.EvidenceRefId;
                Result<EvidenceMarkdown> markdown =
                    await (await _main.ServicesAsync()).Evidence.CreateMarkdownAsync(EvidenceRef);
                if (markdown.IsSuccess)
                {
                    Markdown = markdown.Value.Markdown;
                }
            }

            Raise(nameof(Output));
            Raise(nameof(EvidenceRef));
            Raise(nameof(Markdown));
            await _main.LogOperationAsync("create_evidence_ref", Output);
        });
        MarkdownCommand = new AsyncCommand(async () =>
        {
            Result<EvidenceMarkdown> r = await (await _main.ServicesAsync()).Evidence.CreateMarkdownAsync(EvidenceRef);
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

    public async Task CopyEvidenceRefAsync(string? evidenceRef)
    {
        if (string.IsNullOrWhiteSpace(evidenceRef))
        {
            Output = "ERROR validation_failed: 缺少证据引用。";
            Raise(nameof(Output));
            await _main.LogOperationAsync("copy_evidence_ref", Output);
            return;
        }

        try
        {
            await _main.Clipboard.SetTextAsync(evidenceRef);
            EvidenceRef = evidenceRef;
            Output = "Copied EvidenceRef";
            Raise(nameof(EvidenceRef));
        }
        catch (Exception ex)
        {
            Output = $"ERROR clipboard_unavailable: {ex.Message}";
        }

        Raise(nameof(Output));
        await _main.LogOperationAsync("copy_evidence_ref", Output);
    }

    public async Task CopyEvidenceRefForSearchUnitAsync(SearchMatchedUnitViewModel unit)
    {
        string? evidence = await EnsureEvidenceRefAsync(unit);
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return;
        }

        await CopyEvidenceRefAsync(evidence);
    }

    public async Task CopyEvidenceMarkdownAsync(string? evidenceRef)
    {
        if (string.IsNullOrWhiteSpace(evidenceRef))
        {
            Output = "ERROR validation_failed: 缺少证据引用。";
            Raise(nameof(Output));
            await _main.LogOperationAsync("copy_search_result_evidence_markdown", Output);
            return;
        }

        Result<EvidenceMarkdown> markdown =
            await (await _main.ServicesAsync()).Evidence.CreateMarkdownAsync(evidenceRef);
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
            EvidenceRef = evidenceRef;
            Markdown = markdown.Value.Markdown;
            Output = "Copied Evidence Markdown";
            Raise(nameof(EvidenceRef));
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
        string? evidence = await EnsureEvidenceRefAsync(unit);
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return;
        }

        await CopyEvidenceMarkdownAsync(evidence);
    }

    public async Task<string?> EnsureEvidenceRefAsync(SearchMatchedUnitViewModel unit)
    {
        if (!string.IsNullOrWhiteSpace(unit.EvidenceRef))
        {
            EvidenceRef = unit.EvidenceRef;
            Raise(nameof(EvidenceRef));
            return unit.EvidenceRef;
        }

        try
        {
            Result<EvidenceRefRecord> result =
                await (await _main.ServicesAsync()).Evidence.CreateFromSearchUnitAsync(SearchUnitId.Parse(unit.UnitId));
            if (result.IsFailure)
            {
                Output = $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
                Raise(nameof(Output));
                _main.Report(Output);
                return null;
            }

            unit.EvidenceRef = result.Value.EvidenceRefId;
            EvidenceRef = result.Value.EvidenceRefId;
            Output = "Created EvidenceRef";
            Raise(nameof(EvidenceRef));
            Raise(nameof(Output));
            await _main.LogOperationAsync("create_evidence_ref", EvidenceRef);
            return EvidenceRef;
        }
        catch (Exception ex)
        {
            Output = $"ERROR validation_failed: {ex.Message}";
            Raise(nameof(Output));
            _main.Report(Output);
            return null;
        }
    }

    public void RaiseMarkdown()
    {
        Raise(nameof(Markdown));
    }

    public void RaiseOutput()
    {
        Raise(nameof(Output));
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
                        unit.NodeType,
                        unit.ReadingOrder,
                        unit.IsMatch,
                        null));
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
            EvidenceRef = "";
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
            EvidenceRef = "";
            IndexStatus = "";
            AffectedScopesSummary = "";
            EstimatedTotalText = "";
            Output = $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            _main.Report(Output);
        }

        Raise(nameof(UnitId));
        Raise(nameof(EvidenceRef));
        Raise(nameof(IndexStatus));
        Raise(nameof(AffectedScopesSummary));
        Raise(nameof(EstimatedTotalText));
        Raise(nameof(Results));
        Raise(nameof(HasResults));
        Raise(nameof(HasNoResults));
        Raise(nameof(Output));
    }
}
