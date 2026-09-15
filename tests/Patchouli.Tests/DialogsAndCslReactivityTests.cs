using System.Collections.Concurrent;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Csl;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Microsoft.Data.Sqlite;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Csl;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.Tests;

public sealed class DialogsAndCslReactivityTests
{
    private static BiblatexMappedItem CreateBiblatexItem(string key, string type, string title)
    {
        return new BiblatexMappedItem(
            type,
            null,
            title,
            null,
            null,
            [],
            [],
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            null,
            key,
            type);
    }

    [Fact]
    public void BiblatexImportPreviewDialogViewModel_notifies_selected_entry_changes()
    {
        BiblatexMappedItem item1 = CreateBiblatexItem("key1", "article", "Title 1");
        BiblatexMappedItem item2 = CreateBiblatexItem("key2", "book", "Title 2");

        BiblatexImportPreviewDialogViewModel vm = new([item1, item2], false, "summary");
        ConcurrentQueue<string?> changes = new();
        vm.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        vm.SelectedEntry.Should().NotBeNull();
        vm.SelectedEntry!.Key.Should().Be("key1");

        vm.SelectedEntry = vm.Entries[1];

        vm.SelectedEntry.Key.Should().Be("key2");
        changes.Should().Contain(nameof(vm.SelectedEntry));
    }

    [Fact]
    public void DuplicateItemsDialogViewModel_reactively_updates_counts_and_closes_when_empty()
    {
        ItemId a = ItemId.New();
        ItemId b = ItemId.New();
        DuplicateItemPair pair = new(a, b, [DuplicateItemReason.IdentifierMatch], a);
        Dictionary<ItemId, string> titles = new()
        {
            [a] = "Item A",
            [b] = "Item B"
        };

        DuplicateItemsDialogViewModel vm = new([pair], titles, _ => Task.FromResult(true));
        ConcurrentQueue<string?> changes = new();
        vm.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        vm.HasPairs.Should().BeTrue();
        vm.NoPairs.Should().BeFalse();

        DuplicateItemsDialogResult? result = null;
        vm.RequestClose = res => result = res;

        vm.SkipCommand.Execute(vm.Pairs[0]);

        vm.Pairs.Should().BeEmpty();
        vm.HasPairs.Should().BeFalse();
        vm.NoPairs.Should().BeTrue();
        changes.Should().Contain(nameof(vm.HasPairs));
        changes.Should().Contain(nameof(vm.NoPairs));
        result.Should().Be(DuplicateItemsDialogResult.Closed);
    }

    [Fact]
    public void MergeConflictRowViewModel_reactively_updates_selected_value()
    {
        ItemMergeConflictField field = new("title", "标题", "Local Title", "Incoming Title", "Incoming Title");
        MergeConflictRowViewModel row = new(field);
        ConcurrentQueue<string?> changes = new();
        row.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        row.UseSourceValue.Should().BeTrue();
        row.SelectedValue.Should().Be("Incoming Title");

        row.UseSourceValue = false;

        row.UseSourceValue.Should().BeFalse();
        row.SelectedValue.Should().Be("Local Title");
        changes.Should().Contain(nameof(row.UseSourceValue));
        changes.Should().Contain(nameof(row.SelectedValue));
    }

    [Fact]
    public void ItemMergePreviewDialogViewModel_reactively_updates_conflicts_and_isbusy()
    {
        ItemId sourceId = ItemId.New();
        ItemId targetId = ItemId.New();
        ItemMergePreview preview = new(
            sourceId,
            targetId,
            "Source Item",
            "Target Item",
            [new ItemMergeConflictField("title", "标题", "Target Title", "Source Title", "Target Title")],
            [new ItemMergeMissingField("author", "作者", "Source Author")],
            ["tag1", "tag2"],
            1);

        ItemMergePreviewDialogViewModel vm = new(preview,
            (_, _, _) => Task.FromResult(Result<ItemMergePreview>.Success(preview)));
        ConcurrentQueue<string?> changes = new();
        vm.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        vm.HasConflicts.Should().BeTrue();
        vm.HasMissingFields.Should().BeTrue();
        vm.HasTags.Should().BeTrue();
        vm.HasDocumentsToTransfer.Should().BeTrue();
        vm.CanMerge.Should().BeTrue();
        vm.TagUnionText.Should().Be("tag1, tag2");

        vm.Conflicts.Clear();

        vm.HasConflicts.Should().BeFalse();
        changes.Should().Contain(nameof(vm.HasConflicts));
    }

    [Fact]
    public void BlockingOperationDialogViewModel_reactively_updates_derived_properties()
    {
        BlockingOperationDialogViewModel vm = new();
        ConcurrentQueue<string?> changes = new();
        vm.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        vm.IsDetailsVisible.Should().BeFalse();
        vm.DetailsToggleText.Should().Be("显示详细信息");
        vm.IsRunning.Should().BeTrue();
        vm.IsTerminal.Should().BeFalse();

        vm.IsDetailsVisible = true;

        vm.DetailsToggleText.Should().Be("隐藏详细信息");
        changes.Should().Contain(nameof(vm.IsDetailsVisible));
        changes.Should().Contain(nameof(vm.DetailsToggleText));

        vm.CanCancel = true;
        changes.Should().Contain(nameof(vm.CanCancel));

        vm.MarkCompleted("成功完成");

        vm.IsRunning.Should().BeFalse();
        vm.IsTerminal.Should().BeTrue();
        vm.CanCancel.Should().BeFalse();
        changes.Should().Contain(nameof(vm.IsRunning));
        changes.Should().Contain(nameof(vm.IsTerminal));
    }

    [Fact]
    public void ConflictFieldChoiceViewModel_reactively_toggles_keep_local_and_use_incoming()
    {
        ConflictFieldChoiceViewModel choice = new("item_type", "题录类型", "book", "article");
        ConcurrentQueue<string?> changes = new();
        choice.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

        choice.SelectedSide.Should().Be(BiblatexMappedItemMerge.ChoiceIncoming);
        choice.KeepLocal.Should().BeFalse();
        choice.UseIncoming.Should().BeTrue();

        choice.KeepLocal = true;

        choice.SelectedSide.Should().Be(BiblatexMappedItemMerge.ChoiceLocal);
        choice.KeepLocal.Should().BeTrue();
        choice.UseIncoming.Should().BeFalse();
        changes.Should().Contain(nameof(choice.SelectedSide));
        changes.Should().Contain(nameof(choice.KeepLocal));
        changes.Should().Contain(nameof(choice.UseIncoming));

        choice.UseIncoming = true;

        choice.SelectedSide.Should().Be(BiblatexMappedItemMerge.ChoiceIncoming);
        choice.KeepLocal.Should().BeFalse();
        choice.UseIncoming.Should().BeTrue();
    }

    [Fact]
    public void ConflictResolutionDialogViewModel_updates_action_availability_on_selection()
    {
        ConflictDescriptor descriptor = new(
            ConflictCode.ItemLevelBranch,
            ConflictDomain.SnapshotSync,
            ConflictSeverity.Blocking,
            "Target",
            "Id",
            "Summary",
            null,
            null,
            [new ConflictAction("act1", "动作 1", "说明", true, true)],
            Options:
            [
                new ConflictActionOption("opt1", "选项 1", "描述 1"), new ConflictActionOption("opt2", "选项 2", "描述 2")
            ]);

        ConflictResolutionDialogViewModel vm = new(descriptor);
        vm.HasOptions.Should().BeTrue();
        vm.SelectedOption.Should().BeNull();
        vm.Actions[0].IsEnabled.Should().BeFalse();

        vm.SelectedOption = vm.Options[0];

        vm.Actions[0].IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void CslStyleViewModel_and_CslCatalogStyleViewModel_reactively_update_derived_not_properties()
    {
        CslStyle style = new("style-1", "APA", "en-US", "http://example.com", "source-kind", "hash",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, false);
        CslCatalogStyle catalogStyle = new("style-2", "MLA", "author-date", "http://example.com/mla.csl");

        CslStyleViewModel styleVm = new(style, null!, false);
        ConcurrentQueue<string?> styleChanges = new();
        styleVm.PropertyChanged += (_, args) => styleChanges.Enqueue(args.PropertyName);

        styleVm.IsDefault.Should().BeFalse();
        styleVm.IsNotDefault.Should().BeTrue();

        styleVm.IsDefault = true;

        styleVm.IsDefault.Should().BeTrue();
        styleVm.IsNotDefault.Should().BeFalse();
        styleChanges.Should().Contain(nameof(styleVm.IsDefault));
        styleChanges.Should().Contain(nameof(styleVm.IsNotDefault));

        CslCatalogStyleViewModel catalogVm = new(catalogStyle, null!, false);
        ConcurrentQueue<string?> catalogChanges = new();
        catalogVm.PropertyChanged += (_, args) => catalogChanges.Enqueue(args.PropertyName);

        catalogVm.IsInstalled.Should().BeFalse();
        catalogVm.IsNotInstalled.Should().BeTrue();

        catalogVm.IsInstalled = true;

        catalogVm.IsInstalled.Should().BeTrue();
        catalogVm.IsNotInstalled.Should().BeFalse();
        catalogChanges.Should().Contain(nameof(catalogVm.IsInstalled));
        catalogChanges.Should().Contain(nameof(catalogVm.IsNotInstalled));
    }

    [Fact]
    public async Task CslStyleManagerViewModel_LoadInstalledStylesAsync_does_not_raise_property_changed_for_locale()
    {
        using TemporaryAppSettingsFile settings = new();
        string dbPath = Path.Combine(Path.GetTempPath(), $"patchouli-csl-test-{Guid.NewGuid():N}.sqlite");
        using MainWindowViewModel main = new(settingsPath: settings.Path)
        {
            RuntimeDatabasePath = dbPath
        };
        try
        {
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();

            CslStyleManagerViewModel vm = new(main);
            vm.Locale = "custom-locale";

            ConcurrentQueue<string?> changes = new();
            vm.PropertyChanged += (_, args) => changes.Enqueue(args.PropertyName);

            await vm.RefreshInstalledStylesAsync();

            changes.Should().NotContain(nameof(vm.Locale));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
