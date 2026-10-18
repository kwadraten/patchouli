using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Editor;
using Patchouli.UI.Views;
using CslItemTypeProfileService = Patchouli.Infrastructure.Bibliography.CslItemTypeProfileService;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class ItemEditorNoteSubtitleTests
{
    [Fact]
    public async Task Concrete_types_expose_note_and_subtitle_in_more_fields_but_general_does_not()
    {
        ItemEditorFieldSet general = UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(null);
        general.MoreFields.Should().BeEmpty();
        general.VisibleFields.Should().NotContain(field => field.Key == "Note" || field.Key == "Subtitle");

        Result<CslItemTypeProfile> profile = await new CslItemTypeProfileService().GetProfileAsync("book");
        profile.IsSuccess.Should().BeTrue(profile.ErrorMessage);
        ItemEditorFieldSet fields = UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile.Value);

        fields.MoreFields.Should().Contain(field => field.Key == "Note" && field.Label == "附注" && field.Type == "Note");
        fields.MoreFields.Should()
            .Contain(field => field.Key == "Subtitle" && field.Label == "副标题" && field.Type == "String");
        fields.VisibleFields.Should().NotContain(field => field.Key == "Note" || field.Key == "Subtitle");
    }

    [Fact]
    public async Task Every_concrete_type_has_note_and_subtitle_once_and_keeps_citation_key_hidden()
    {
        Result<IReadOnlyList<CslItemTypeProfile>> profiles =
            await new CslItemTypeProfileService().ListProfilesAsync();
        profiles.IsSuccess.Should().BeTrue(profiles.ErrorMessage);

        foreach (CslItemTypeProfile profile in profiles.Value.Where(profile => profile.ItemType != "general"))
        {
            ItemEditorFieldSet fields = UI.ViewModels.Editor.CslItemTypeProfileService.GetProfile(profile);
            ItemFieldDefinition[] allFields = fields.VisibleFields.Concat(fields.MoreFields).ToArray();
            allFields.Count(field => field.Key == "Note").Should().Be(1, profile.ItemType);
            allFields.Count(field => field.Key == "Subtitle").Should().Be(1, profile.ItemType);
            allFields.Should().NotContain(field => field.Key == "CitationKey" || field.Key == "citation-key",
                profile.ItemType);
            fields.MoreFields.Should().Contain(field => field.Key == "Note" && field.Label == "附注");
            fields.MoreFields.Should().Contain(field => field.Key == "Subtitle" && field.Label == "副标题");
        }
    }

    [Fact]
    public async Task Note_and_subtitle_edit_clear_and_biblatex_round_trip_in_headless_editor()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            string databasePath = Path.Combine(Path.GetTempPath(), $"editor-note-subtitle-{Guid.NewGuid():N}.sqlite");
            await using MainWindowViewModel main = new(settingsPath: settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            try
            {
                await main.OpenDatabaseCommand.ExecuteAsync();
                await main.Library.CreateCommand.ExecuteAsync();
                HostServices services = await main.ServicesAsync();
                Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                    new CreateItemRequest("book", "附注与副标题测试"));
                created.IsSuccess.Should().BeTrue(created.ErrorMessage);

                await main.EditItemByIdAsync(created.Value.ItemId.ToString());
                main.ItemEditor.ActiveNavSection = main.ItemEditor.NavSections
                    .Single(section => section.Title == "题录与笔记");
                main.ItemEditor.IsNotesSectionActive.Should().BeTrue();
                main.ItemEditor.IsExtendedSectionActive.Should().BeFalse();
                main.ItemEditor.NoteFields.Should().ContainSingle(field => field.Key == "Note");
                main.ItemEditor.ExtendedFields.Should().NotContain(field => field.Key == "Note");
                main.ItemEditor.ExtendedFields.Should().Contain(field => field.Key == "Subtitle");
                ItemFieldDescriptor note = main.ItemEditor.MoreFields.Single(field => field.Key == "Note");
                ItemFieldDescriptor subtitle = main.ItemEditor.MoreFields.Single(field => field.Key == "Subtitle");
                note.Value.Should().BeEmpty();
                subtitle.Value.Should().BeEmpty();

                ItemEditorPage page = new() { DataContext = main.ItemEditor };
                Window window = new() { Content = page, Width = 1000, Height = 900 };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    LineNumberedTextEditor noteEditor = window.GetVisualDescendants()
                        .OfType<LineNumberedTextEditor>()
                        .Single(editor => ReferenceEquals(editor.DataContext, note));
                    noteEditor.ShowLineNumbers.Should().BeTrue();
                    noteEditor.SyntaxHighlighting.Should().BeNull();
                    noteEditor.ContentText.Should().Be(note.Value);
                    noteEditor.Document.LineCount.Should().Be(1);
                    noteEditor.IsEffectivelyVisible.Should().BeTrue();
                    window.GetVisualDescendants().OfType<TextBlock>()
                        .Where(block => block.Text == "集合").Should()
                        .OnlyContain(block => !block.IsEffectivelyVisible);
                    main.ItemEditor.ActiveNavSection = main.ItemEditor.NavSections
                        .Single(section => section.Content is ItemEditorSection.ExtendedInformation);
                    Dispatcher.UIThread.RunJobs();
                    main.ItemEditor.IsNotesSectionActive.Should().BeFalse();
                    noteEditor.IsEffectivelyVisible.Should().BeFalse();
                    main.ItemEditor.ActiveNavSection = main.ItemEditor.NavSections
                        .Single(section => section.Content is ItemEditorSection.BibliographyAndNotes);
                    Dispatcher.UIThread.RunJobs();
                    noteEditor.IsEffectivelyVisible.Should().BeTrue();

                    const string editedNote = "修改后的附注第一行\n修改后的附注第二行\n修改后的附注第三行";
                    noteEditor.Document.Text = editedNote;
                    noteEditor.Document.UndoStack.CanUndo.Should().BeTrue();
                    note.Value = "由外部加载的新题录附注";
                    Dispatcher.UIThread.RunJobs();
                    noteEditor.ContentText.Should().Be(note.Value);
                    noteEditor.Document.UndoStack.CanUndo.Should().BeFalse();
                    noteEditor.Document.Text = editedNote;
                    subtitle.Value = "修改后的副标题";
                    Dispatcher.UIThread.RunJobs();
                    note.Value.Should().Be(editedNote);
                    window.UpdateLayout();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using (WriteableBitmap? frame = window.CaptureRenderedFrame())
                    {
                        frame.Should().NotBeNull();
                        string previewPath =
                            TestPaths.FromRepositoryRoot(".tmp", "bug9-12", "bibliography-and-notes.png");
                        Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
                        frame!.Save(previewPath);
                    }

                    await main.ItemEditor.SaveCommand.ExecuteAsync();
                    Result<ItemMetadata> saved = await services.Items.GetItemAsync(created.Value.ItemId);
                    saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
                    saved.Value.Note.Should().Be(editedNote);
                    saved.Value.Subtitle.Should().Be("修改后的副标题");

                    await main.ItemEditor.LoadAsync(created.Value.ItemId.ToString());
                    main.ItemEditor.MoreFields.Single(field => field.Key == "Note").Value.Should().Be(editedNote);
                    main.ItemEditor.MoreFields.Single(field => field.Key == "Subtitle").Value
                        .Should().Be("修改后的副标题");

                    Result<string> exported = await services.BiblatexImport.ExportItemsAsync([created.Value.ItemId]);
                    exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
                    Result<IReadOnlyList<BiblatexEntryDto>> parsed =
                        await services.BiblatexImport.ParseTextAsync(exported.Value);
                    parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
                    parsed.Value.Should().ContainSingle();
                    exported.Value.Should().Contain("\\newline{}");
                    Result<BiblatexMappedItem> remappedExport =
                        BiblatexFieldMapper.MapVisibleEntry(parsed.Value[0]);
                    remappedExport.IsSuccess.Should().BeTrue(remappedExport.ErrorMessage);
                    remappedExport.Value.Note.Should().Be(editedNote);
                    parsed.Value[0].Fields["subtitle"].Should().Be("修改后的副标题");

                    main.ItemEditor.MoreFields.Single(field => field.Key == "Note").Value = "";
                    main.ItemEditor.MoreFields.Single(field => field.Key == "Subtitle").Value = "";
                    await main.ItemEditor.SaveCommand.ExecuteAsync();
                    await main.ItemEditor.LoadAsync(created.Value.ItemId.ToString());
                    main.ItemEditor.MoreFields.Single(field => field.Key == "Note").Value.Should().BeEmpty();
                    main.ItemEditor.MoreFields.Single(field => field.Key == "Subtitle").Value.Should().BeEmpty();
                    Result<ItemMetadata> cleared = await services.Items.GetItemAsync(created.Value.ItemId);
                    cleared.Value.Note.Should().BeNullOrEmpty();
                    cleared.Value.Subtitle.Should().BeNullOrEmpty();
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                SqliteTestCleanup.ReleasePools(databasePath);
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Biblatex_note_and_subtitle_import_into_fields_and_survive_editor_save_and_export()
    {
        File.Exists(BiblatexHelperClient.ResolveDefaultHelperPath())
            .Should().BeTrue("the BibLaTeX helper is required to verify note line-break round trips");

        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            string databasePath = Path.Combine(Path.GetTempPath(), $"biblatex-note-subtitle-{Guid.NewGuid():N}.sqlite");
            await using MainWindowViewModel main = new(settingsPath: settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            try
            {
                await main.OpenDatabaseCommand.ExecuteAsync();
                await main.Library.CreateCommand.ExecuteAsync();
                HostServices services = await main.ServicesAsync();
                const string source = """
                                      @book{note-subtitle-example,
                                        title = {示例标题},
                                        subtitle = {导入的副标题},
                                        author = {Zhang, San},
                                        date = {2026},
                                        publisher = {示例出版社},
                                        note = {导入附注第一行。
                                      导入附注第二行。}
                                      }
                                      """;
                Result<IReadOnlyList<BiblatexEntryDto>> parsedSource =
                    await services.BiblatexImport.ParseTextAsync(source);
                parsedSource.IsSuccess.Should().BeTrue(parsedSource.ErrorMessage);
                Result<BiblatexSingleImportPreview> preview =
                    await services.BiblatexImport.PreviewSingleAsync(parsedSource.Value.Single(), null);
                preview.IsSuccess.Should().BeTrue(preview.ErrorMessage);
                Result<BiblatexImportApplyResult> applied = await services.BiblatexImport.ApplySingleAsync(
                    preview.Value.Source, null, null, null);
                applied.IsSuccess.Should().BeTrue(applied.ErrorMessage);
                ItemId importedId = ItemId.Parse(applied.Value.CreatedItemIds.Single());

                await main.EditItemByIdAsync(importedId.ToString());
                main.ItemEditor.MoreFields.Single(field => field.Key == "Note").Value
                    .Should().Be("导入附注第一行。 导入附注第二行。");
                main.ItemEditor.MoreFields.Single(field => field.Key == "Subtitle").Value.Should().Be("导入的副标题");

                main.ItemEditor.MoreFields.Single(field => field.Key == "Note").Value = "保存的附注第一行\n保存的附注第二行";
                main.ItemEditor.MoreFields.Single(field => field.Key == "Subtitle").Value = "保存的副标题";
                await main.ItemEditor.SaveCommand.ExecuteAsync();
                await main.ItemEditor.LoadAsync(importedId.ToString());
                main.ItemEditor.MoreFields.Single(field => field.Key == "Note").Value
                    .Should().Be("保存的附注第一行\n保存的附注第二行");
                main.ItemEditor.MoreFields.Single(field => field.Key == "Subtitle").Value.Should().Be("保存的副标题");

                Result<string> exported = await services.BiblatexImport.ExportItemsAsync([importedId]);
                exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
                Result<IReadOnlyList<BiblatexEntryDto>> parsedExport =
                    await services.BiblatexImport.ParseTextAsync(exported.Value);
                parsedExport.IsSuccess.Should().BeTrue(parsedExport.ErrorMessage);
                exported.Value.Should().Contain("\\newline{}");
                Result<BiblatexMappedItem> remapped = BiblatexFieldMapper.MapVisibleEntry(parsedExport.Value.Single());
                remapped.IsSuccess.Should().BeTrue(remapped.ErrorMessage);
                remapped.Value.Note.Should().Be("保存的附注第一行\n保存的附注第二行");
                parsedExport.Value.Single().Fields["subtitle"].Should().Be("保存的副标题");
            }
            finally
            {
                SqliteTestCleanup.ReleasePools(databasePath);
            }

            return 0;
        }, CancellationToken.None);
    }
}
