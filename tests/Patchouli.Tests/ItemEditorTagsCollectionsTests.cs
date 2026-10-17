using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Editor;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class ItemEditorTagsCollectionsTests
{
    [Fact]
    public async Task Classification_section_matches_sidebar_tag_chips_and_stages_tags_and_collections_until_save()
    {
        await RunAsync(async (main, services) =>
        {
            Result<Collection> collection = await services.Collections.CreateCollectionAsync("待读文献");
            collection.IsSuccess.Should().BeTrue();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(new CreateItemRequest("book", "分类编辑",
                TagsJson: JsonSerializer.Serialize(new[] { "原标签", "逗号,分号;保留" })));
            created.IsSuccess.Should().BeTrue();
            await main.EditItemByIdAsync(created.Value.ItemId.ToString());
            ItemEditorViewModel editor = main.ItemEditor;
            editor.Fields.Concat(editor.MoreFields).Should().NotContain(descriptor => descriptor.Key == "TagsText");
            editor.ActiveNavSection = editor.NavSections.Single(section => section.Title == "标签、集合与标识符");
            Window window = new()
                { Width = 1120, Height = 1000, Content = new ItemEditorPage { DataContext = editor } };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                foreach (string title in new[] { "标签", "集合", "标识符" })
                {
                    window.GetVisualDescendants().OfType<TextBlock>().Should()
                        .Contain(block => block.Text == title && block.IsEffectivelyVisible);
                }

                Button toggle = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "ToggleEditorTagInput");
                toggle.Command!.Execute(toggle.CommandParameter);
                editor.IsTagEditorOpen.Should().BeTrue();
                TextBox input = window.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.Name == "EditorTagNameInput");
                input.Text = "  新标签  ";
                Button add = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "AddEditorTagButton");
                add.Command!.Execute(add.CommandParameter);
                editor.Tags.Select(tag => tag.Name).Should().Equal("原标签", "逗号,分号;保留", "新标签");
                input.Text = "新标签";
                add.Command.Execute(add.CommandParameter);
                editor.Tags.Should().HaveCount(3);
                Button remove = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "RemoveEditorTagButton" &&
                                      button.DataContext is EditorTagViewModel tag && tag.Name == "原标签");
                remove.Command!.Execute(remove.CommandParameter);
                window.GetVisualDescendants().OfType<CheckBox>()
                    .Single(box => box.DataContext is CollectionSelectionItemViewModel row && row.Name == "待读文献")
                    .IsChecked = true;
                editor.HasUnsavedChanges.Should().BeTrue();
                JsonSerializer
                    .Deserialize<string[]>((await services.Items.GetItemAsync(created.Value.ItemId)).Value.TagsJson)
                    .Should().Equal("原标签", "逗号,分号;保留");
                (await services.Collections.GetItemCollectionIdsAsync(created.Value.ItemId)).Value.Should().BeEmpty();

                await editor.SaveCommand.ExecuteAsync();
                editor.Tags.Select(tag => tag.Name).Should().Equal("逗号,分号;保留", "新标签");
                (await services.Collections.GetItemCollectionIdsAsync(created.Value.ItemId)).Value
                    .Should().ContainSingle(id => id == collection.Value.CollectionId);
                editor.HasUnsavedChanges.Should().BeFalse();
                editor.Tags[0].RemoveCommand.Execute(null);
                editor.Collections.Single().IsSelected = false;
                await editor.DiscardCommand.ExecuteAsync();
                editor.Tags.Select(tag => tag.Name).Should().Equal("逗号,分号;保留", "新标签");
                editor.Collections.Single().IsSelected.Should().BeTrue();
                editor.HasUnsavedChanges.Should().BeFalse();

                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using (WriteableBitmap? frame = window.CaptureRenderedFrame())
                {
                    frame.Should().NotBeNull();
                    string preview =
                        TestPaths.FromRepositoryRoot(".tmp", "bug9-12", "tags-collections-identifiers.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
                    frame!.Save(preview);
                }

                Border collectionCard = window.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "EditorCollectionsCard");
                foreach (NavCategoryViewModel section in editor.NavSections)
                {
                    editor.ActiveNavSection = section;
                    Dispatcher.UIThread.RunJobs();
                    collectionCard.IsEffectivelyVisible.Should()
                        .Be(section.Content is ItemEditorSection.Identifiers);
                }

                foreach (EditorTagViewModel tag in editor.Tags.ToArray())
                {
                    tag.RemoveCommand.Execute(null);
                }

                editor.HasNoTags.Should().BeTrue();
                await editor.SaveCommand.ExecuteAsync();
                await editor.LoadAsync(created.Value.ItemId.ToString());
                editor.Tags.Should().BeEmpty();
                JsonSerializer
                    .Deserialize<string[]>((await services.Items.GetItemAsync(created.Value.ItemId)).Value.TagsJson)
                    .Should().BeEmpty();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task New_item_tags_survive_type_changes_and_reset_when_starting_another_item()
    {
        await RunAsync(async (main, services) =>
        {
            await main.CreateItemMenuCommand.ExecuteAsync();
            ItemEditorViewModel editor = main.ItemEditor;
            editor.Title = "新题录标签";
            editor.NewTagName = "保留,完整标签";
            editor.AddTagCommand.Execute(null);
            editor.Tags.Should().ContainSingle();
            editor.ItemType = "book";
            for (int retry = 0;
                 retry < 100 && !editor.MoreFields.Any(descriptor => descriptor.Key == "Subtitle");
                 retry++)
            {
                await Task.Delay(20);
            }

            editor.MoreFields.Should().Contain(descriptor => descriptor.Key == "Subtitle");
            editor.Tags.Should().ContainSingle(tag => tag.Name == "保留,完整标签");
            await editor.SaveCommand.ExecuteAsync();
            ItemId itemId = ItemId.Parse(editor.ItemIdText);
            JsonSerializer.Deserialize<string[]>((await services.Items.GetItemAsync(itemId)).Value.TagsJson)
                .Should().Equal("保留,完整标签");
            await editor.NewAsync();
            editor.Tags.Should().BeEmpty();
            editor.HasNoTags.Should().BeTrue();
            editor.IsTagEditorOpen.Should().BeFalse();
            editor.NewTagName.Should().BeEmpty();
            editor.HasUnsavedChanges.Should().BeFalse();
        });
    }

    private static async Task RunAsync(Func<MainWindowViewModel, HostServices, Task> action)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            string database = Path.Combine(Path.GetTempPath(), $"editor-classification-{Guid.NewGuid():N}.sqlite");
            await using MainWindowViewModel main = new(settingsPath: settings.Path) { RuntimeDatabasePath = database };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            await action(main, await main.ServicesAsync());
            return 0;
        }, CancellationToken.None);
    }
}
