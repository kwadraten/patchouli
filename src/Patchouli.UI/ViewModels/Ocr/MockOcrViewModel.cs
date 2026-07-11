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

public sealed class MockOcrViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    public string Name { get; set; } = "Mock preset";
    public string PresetId { get; set; } = "";
    public string DocumentInstanceId { get; set; } = "";
    public string PageIds { get; set; } = "";
    public string ImagePageId { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string RunId { get; set; } = "";
    public string NewModelPath { get; set; } = "";
    public bool ApplyOnSuccess { get; set; } = true;
    public string ParametersJson { get; set; } = "{}";
    public string Output { get; set; } = "";
    public string Capabilities { get; set; } = "";
    public ObservableCollection<string> RecentRuns { get; } = new();
    public AsyncCommand CreatePresetCommand { get; }
    public AsyncCommand RunCommand { get; }
    public AsyncCommand RunImageCommand { get; }
    public AsyncCommand ShowRunCommand { get; }
    public AsyncCommand AdoptCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand UnsetCurrentCommand { get; }
    public AsyncCommand HideRunCommand { get; }
    public AsyncCommand ShowCapabilitiesCommand { get; }
    public AsyncCommand CheckEnvironmentCommand { get; }
    public AsyncCommand RebindModelPathCommand { get; }

    public MockOcrViewModel(MainWindowViewModel m)
    {
        _main = m;
        CreatePresetCommand = new AsyncCommand(async () =>
        {
            Result<OcrPreset> r = await (await _main.ServicesAsync()).OcrPresets.CreatePresetAsync(Name, null,
                OcrEngineIds.Mock, OcrModelIds.MockBasic, null, ParametersJson, ApplyOnSuccess);
            if (r.IsSuccess)
            {
                PresetId = r.Value.PresetId.ToString();
                Raise(nameof(PresetId));
            }

            Output = r.IsSuccess ? $"Preset: {r.Value.PresetId}" : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
        });
        RunCommand = new AsyncCommand(async () =>
        {
            PageId[] pages = PageIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(PageId.Parse).ToArray();
            Result<OcrRun> r = await (await _main.ServicesAsync()).Ocr.RunPresetOnPagesAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), OcrPresetId.Parse(PresetId), pages);
            if (r.IsSuccess)
            {
                RunId = r.Value.OcrRunId.ToString();
                RecentRuns.Add($"{r.Value.OcrRunId} | {r.Value.State}");
                Raise(nameof(RunId));
            }

            Output = r.IsSuccess
                ? $"Run: {r.Value.OcrRunId}\n{r.Value.State}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("run_mock_ocr", Output);
        });
        RunImageCommand = new AsyncCommand(async () =>
        {
            Result<OcrRun> r = await (await _main.ServicesAsync()).Ocr.RunPresetOnImagePageAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), OcrPresetId.Parse(PresetId),
                PageId.Parse(ImagePageId), ImagePath);
            if (r.IsSuccess)
            {
                RunId = r.Value.OcrRunId.ToString();
                RecentRuns.Add($"{r.Value.OcrRunId} | {r.Value.State}");
                Raise(nameof(RunId));
            }

            Output = r.IsSuccess
                ? $"Image OCR run: {r.Value.OcrRunId}\n{r.Value.State}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("run_local_image_ocr", Output);
        });
        ShowRunCommand = new AsyncCommand(async () =>
        {
            AppServices s = await _main.ServicesAsync();
            Result<OcrRun> run = await s.Ocr.GetRunAsync(OcrRunId.Parse(RunId));
            Result<IReadOnlyList<OcrPageResult>> pages = await s.Ocr.ListPageResultsAsync(OcrRunId.Parse(RunId));
            Output = run.IsSuccess
                ? $"{run.Value.State}\n" + string.Join("\n",
                    pages.Value.Select(p => $"{p.PageId}: {p.State} {p.ErrorCode} {p.ErrorMessage}"))
                : $"ERROR {run.ErrorCode}: {run.ErrorMessage}";
            Raise(nameof(Output));
        });
        AdoptCommand = new AsyncCommand(async () =>
        {
            PageId[]? selected = string.IsNullOrWhiteSpace(PageIds)
                ? null
                : PageIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(PageId.Parse).ToArray();
            Result<OcrCandidateAdoption> r =
                await (await _main.ServicesAsync()).Ocr.AdoptCandidateRunAsync(OcrRunId.Parse(RunId), selected);
            Output = r.IsSuccess ? $"Adopted: {r.Value.AdoptedRevisionId}" : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
        });
        CancelCommand = new AsyncCommand(async () =>
        {
            Result r = await (await _main.ServicesAsync()).Ocr.CancelRunAsync(OcrRunId.Parse(RunId));
            Output = r.IsSuccess ? "Run cancelled." : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
        });
        UnsetCurrentCommand = new AsyncCommand(async () =>
        {
            Result r = await (await _main.ServicesAsync()).Ocr.UnsetCurrentOcrAsync(
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));
            Output = r.IsSuccess ? "Current OCR revision unset." : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("unset_current_ocr", Output);
        });
        HideRunCommand = new AsyncCommand(async () =>
        {
            Result r = await (await _main.ServicesAsync()).Ocr.HideOcrRunAsync(OcrRunId.Parse(RunId));
            Output = r.IsSuccess ? "OCR run hidden." : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("hide_ocr_run", Output);
        });
        ShowCapabilitiesCommand = new AsyncCommand(async () =>
        {
            Capabilities = string.Join("\n",
                (await _main.ServicesAsync()).OcrAdapters.ListCapabilities().Select(c =>
                    $"{c.EngineId}: {c.DisplayName}; requires model path={c.RequiresModelPath}; {c.Notes}"));
            Raise(nameof(Capabilities));
        });
        CheckEnvironmentCommand = new AsyncCommand(async () =>
        {
            AppServices s = await _main.ServicesAsync();
            Result<OcrPresetVersion> version = await s.OcrPresets.GetCurrentVersionAsync(OcrPresetId.Parse(PresetId));
            if (version.IsFailure)
            {
                Output = $"ERROR {version.ErrorCode}: {version.ErrorMessage}";
            }
            else
            {
                Result<OcrEnvironmentCheckResult> check =
                    await s.OcrAdapters.CheckEngineAsync(version.Value.EngineId, version.Value);
                Output = check.IsSuccess
                    ? $"{check.Value.Status}\n{check.Value.Message}\nAction: {check.Value.RequiredAction}"
                    : $"ERROR {check.ErrorCode}: {check.ErrorMessage}";
            }

            Raise(nameof(Output));
        });
        RebindModelPathCommand = new AsyncCommand(async () =>
        {
            Result<OcrPresetVersion> r =
                await (await _main.ServicesAsync()).OcrPresets.RebindModelPathAsync(OcrPresetId.Parse(PresetId),
                    NewModelPath);
            Output = r.IsSuccess
                ? $"Rebound model path. New preset version: {r.Value.PresetVersionId}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
        });
    }
}
