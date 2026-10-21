using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Settings;

namespace Patchouli.Tests;

/// <summary>
/// Issue #11: per-action file handling behavior settings are device-local,
/// default to keep-in-place, and are configured independently per action.
/// </summary>
public sealed class FileHandlingBehaviorSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"patchouli-behavior-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
    }

    private string SettingsPath => Path.Combine(_root, "settings.json");

    [Fact]
    public void Default_keeps_both_actions_in_place()
    {
        FileHandlingBehaviorSettings settings = FileHandlingBehaviorSettings.Default();

        settings.McpAddPdfBehavior.Should().Be(FileHandlingBehavior.KeepInPlace);
        settings.PdfDragDropBehavior.Should().Be(FileHandlingBehavior.KeepInPlace);
    }

    [Fact]
    public async Task Missing_settings_file_loads_defaults()
    {
        FileHandlingBehaviorSettingsService service = new(SettingsPath, new SystemClock());

        Result<FileHandlingBehaviorSettings> result = await service.GetSettingsAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(FileHandlingBehaviorSettings.Default());
    }

    [Fact]
    public async Task Save_and_load_round_trips_independent_actions()
    {
        FileHandlingBehaviorSettingsService service = new(SettingsPath, new SystemClock());
        FileHandlingBehaviorSettings settings = new(
            FileHandlingBehavior.MoveToFirstSearchRoot,
            FileHandlingBehavior.KeepInPlace);

        Result<FileHandlingBehaviorSettings> saved = await service.SaveSettingsAsync(settings);
        Result<FileHandlingBehaviorSettings> loaded = await service.GetSettingsAsync();

        saved.IsSuccess.Should().BeTrue();
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.McpAddPdfBehavior.Should().Be(FileHandlingBehavior.MoveToFirstSearchRoot);
        // The drag-drop action is untouched by the MCP add change.
        loaded.Value.PdfDragDropBehavior.Should().Be(FileHandlingBehavior.KeepInPlace);
    }

    [Fact]
    public async Task Legacy_file_without_section_falls_back_to_defaults()
    {
        await File.WriteAllTextAsync(SettingsPath, """{"Other": {"Key": "value"}}""");
        FileHandlingBehaviorSettingsService service = new(SettingsPath, new SystemClock());

        Result<FileHandlingBehaviorSettings> result = await service.GetSettingsAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(FileHandlingBehaviorSettings.Default());
    }

    [Fact]
    public async Task Unknown_enum_text_falls_back_to_keep_in_place()
    {
        await File.WriteAllTextAsync(SettingsPath,
            """{"FileHandlingBehavior": {"McpAddPdfBehavior": "Teleport", "PdfDragDropBehavior": "MoveToFirstSearchRoot"}}""");
        FileHandlingBehaviorSettingsService service = new(SettingsPath, new SystemClock());

        Result<FileHandlingBehaviorSettings> result = await service.GetSettingsAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.McpAddPdfBehavior.Should().Be(FileHandlingBehavior.KeepInPlace);
        result.Value.PdfDragDropBehavior.Should().Be(FileHandlingBehavior.MoveToFirstSearchRoot);
    }

    [Fact]
    public async Task Save_preserves_other_sections_in_shared_device_file()
    {
        await File.WriteAllTextAsync(SettingsPath, """{"McpServer": {"Port": 1234}}""");
        FileHandlingBehaviorSettingsService service = new(SettingsPath, new SystemClock());

        await service.SaveSettingsAsync(new FileHandlingBehaviorSettings(
            FileHandlingBehavior.MoveToFirstSearchRoot, FileHandlingBehavior.MoveToFirstSearchRoot));

        string json = await File.ReadAllTextAsync(SettingsPath);
        json.Should().Contain("1234");
        json.Should().Contain("MoveToFirstSearchRoot");
    }

    [Fact]
    public async Task Save_rejects_null_settings()
    {
        FileHandlingBehaviorSettingsService service = new(SettingsPath, new SystemClock());

        await FluentActions.Invoking(() => service.SaveSettingsAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Enum_keep_in_place_is_zero_for_stable_defaults()
    {
        ((int)FileHandlingBehavior.KeepInPlace).Should().Be(0);
    }
}
