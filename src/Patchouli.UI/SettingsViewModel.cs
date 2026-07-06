using System.Collections.ObjectModel;
using Dapper;
using Patchouli.Ocr;

namespace Patchouli.UI;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public SettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        SaveMinerUSettingsCommand = new AsyncCommand(SaveMinerUSettingsAsync);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        AddFileSearchRootCommand = new AsyncCommand(AddFileSearchRootAsync);
        CreateOcrPresetCommand = new AsyncCommand(CreateOcrPresetAsync);
        CreateSearchProfileCommand = new AsyncCommand(CreateSearchProfileAsync);
        StartMcpCommand = new AsyncCommand(StartMcpAsync);
        StopMcpCommand = new AsyncCommand(StopMcpAsync);
    }

    public string SelectedSection { get; private set; } = "mineru";
    public string MinerUTokenInput { get; set; } = "";
    public string Status { get; private set; } = "在这里管理 MinerU OCR 凭据。";
    public string SettingsFilePath => _main.SettingsFilePath;
    public string RuntimeDatabasePath => _main.RuntimeDatabasePath;
    public string DefaultSyncRootPath => _main.DefaultSyncRootPath;
    public string McpEndpoint => _main.McpEndpoint;
    public string McpStatusText => _main.McpStatusText;
    public string OcrConcurrencySummary { get; private set; } = "OCR 队列第一版使用本机单任务 tick 执行。";
    public string FileSearchRootInput { get; set; } = "";
    public string OcrPresetName { get; set; } = "Local OCR";
    public string OcrPresetDescription { get; set; } = "";
    public string OcrPresetEngineId { get; set; } = OcrEngineIds.LocalPlaceholder;
    public string OcrPresetModelId { get; set; } = OcrModelIds.MockBasic;
    public string OcrPresetParametersJson { get; set; } = "{}";
    public bool OcrPresetApplyOnSuccess { get; set; } = true;
    public string SearchProfileName { get; set; } = "";
    public string SearchProfileDescription { get; set; } = "";
    public ObservableCollection<string> FileSearchRoots { get; } = new();
    public ObservableCollection<string> OcrPresets { get; } = new();
    public ObservableCollection<string> SearchProfiles { get; } = new();
    public bool ShowMinerUSection => SelectedSection == "mineru";
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SaveMinerUSettingsCommand { get; }
    public AsyncCommand AddFileSearchRootCommand { get; }
    public AsyncCommand CreateOcrPresetCommand { get; }
    public AsyncCommand CreateSearchProfileCommand { get; }
    public AsyncCommand StartMcpCommand { get; }
    public AsyncCommand StopMcpCommand { get; }

    public void FocusMinerU(string? status = null)
    {
        SelectedSection = "mineru";
        if (!string.IsNullOrWhiteSpace(status))
            Status = status;
        Raise(nameof(SelectedSection));
        Raise(nameof(ShowMinerUSection));
        Raise(nameof(Status));
    }

    public void SyncFromCurrentSettings(string token)
    {
        MinerUTokenInput = token;
        Raise(nameof(MinerUTokenInput));
        Raise(nameof(SettingsFilePath));
    }

    public async Task RefreshAsync()
    {
        try
        {
            var services = await _main.ServicesAsync();
            FileSearchRoots.Clear();
            OcrPresets.Clear();
            SearchProfiles.Clear();

            var roots = await services.FileResolution.ListSearchRootsAsync();
            if (roots.IsSuccess)
            {
                foreach (var root in roots.Value)
                {
                    FileSearchRoots.Add($"{root.RootPath} | {(root.IsAvailable ? "available" : "offline")} | {root.UpdatedAt:yyyy-MM-dd HH:mm}");
                }
            }

            await using var connection = services.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            var presets = await connection.QueryAsync<PresetRow>(
                """
                select p.name as Name, coalesce(v.engine_id, '') as EngineId, coalesce(v.model_id, '') as ModelId,
                       p.archived as Archived
                from ocr_presets p
                left join ocr_preset_versions v on v.preset_version_id = p.current_version_id
                order by p.archived, p.name;
                """);
            foreach (var preset in presets)
            {
                OcrPresets.Add($"{preset.Name} | {preset.EngineId}/{preset.ModelId} | {(preset.Archived == 0 ? "active" : "archived")}");
            }

            var profiles = await services.SearchProfiles.ListProfilesAsync(includeArchived: true);
            if (profiles.IsSuccess)
            {
                foreach (var profile in profiles.Value)
                {
                    SearchProfiles.Add($"{profile.Name} | {(profile.IsDefault ? "default" : "profile")} | {(profile.Archived ? "archived" : "active")}");
                }
            }

            Status = "设置已刷新。";
            RaiseAll();
        }
        catch (Exception ex)
        {
            Status = $"设置刷新失败：{ex.Message}";
            Raise(nameof(Status));
        }
    }

    private async Task SaveMinerUSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(MinerUTokenInput))
        {
            Status = "请先填写 MinerU API token。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        var saved = await _main.SaveMinerUTokenSettingsAsync(MinerUTokenInput);
        Status = saved
            ? "MinerU 凭据已保存到 ProviderCredential 和 appsettings。"
            : "MinerU 凭据保存失败。";
        Raise(nameof(Status));
    }

    private async Task AddFileSearchRootAsync()
    {
        if (string.IsNullOrWhiteSpace(FileSearchRootInput))
        {
            Status = "请填写 FileSearchRoot 路径。";
            Raise(nameof(Status));
            return;
        }

        var result = await (await _main.ServicesAsync()).FileResolution.AddSearchRootAsync(FileSearchRootInput);
        var message = result.IsSuccess ? "FileSearchRoot 已添加。" : result.ErrorMessage ?? "FileSearchRoot 添加失败。";
        await RefreshAsync();
        Status = message;
        Raise(nameof(Status));
        _main.Report(message);
    }

    private async Task CreateOcrPresetAsync()
    {
        var result = await (await _main.ServicesAsync()).OcrPresets.CreatePresetAsync(
            OcrPresetName,
            OcrPresetDescription,
            OcrPresetEngineId,
            OcrPresetModelId,
            modelPath: null,
            OcrPresetParametersJson,
            OcrPresetApplyOnSuccess);
        var message = result.IsSuccess ? "OCR Preset 已创建。" : result.ErrorMessage ?? "OCR Preset 创建失败。";
        await RefreshAsync();
        Status = message;
        Raise(nameof(Status));
        _main.Report(message);
    }

    private async Task CreateSearchProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchProfileName))
        {
            Status = "请填写 SearchProfile 名称。";
            Raise(nameof(Status));
            return;
        }

        var result = await (await _main.ServicesAsync()).SearchProfiles.CreateProfileAsync(SearchProfileName, SearchProfileDescription);
        var message = result.IsSuccess ? "SearchProfile 已创建。" : result.ErrorMessage ?? "SearchProfile 创建失败。";
        await RefreshAsync();
        Status = message;
        Raise(nameof(Status));
        _main.Report(message);
    }

    private async Task StartMcpAsync()
    {
        await _main.StartMcpServerAsync();
        Raise(nameof(McpEndpoint));
        Raise(nameof(McpStatusText));
    }

    private async Task StopMcpAsync()
    {
        await _main.StopMcpServerAsync();
        Raise(nameof(McpEndpoint));
        Raise(nameof(McpStatusText));
    }

    private void RaiseAll()
    {
        foreach (var property in new[]
        {
            nameof(Status), nameof(RuntimeDatabasePath), nameof(DefaultSyncRootPath), nameof(McpEndpoint),
            nameof(McpStatusText), nameof(OcrConcurrencySummary), nameof(FileSearchRootInput), nameof(OcrPresetName),
            nameof(OcrPresetDescription), nameof(OcrPresetEngineId), nameof(OcrPresetModelId),
            nameof(OcrPresetParametersJson), nameof(OcrPresetApplyOnSuccess), nameof(SearchProfileName),
            nameof(SearchProfileDescription), nameof(FileSearchRoots), nameof(OcrPresets), nameof(SearchProfiles),
            nameof(SettingsFilePath)
        })
        {
            Raise(property);
        }
    }

    private sealed class PresetRow
    {
        public string Name { get; set; } = "";
        public string EngineId { get; set; } = "";
        public string ModelId { get; set; } = "";
        public int Archived { get; set; }
    }
}
