using Avalonia.Controls;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.UI;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class DialogCloseBindingTests
{
    [Theory]
    [InlineData("Confirm")]
    [InlineData("BiblatexImportPreview")]
    [InlineData("ConflictResolution")]
    [InlineData("DuplicateItems")]
    [InlineData("ItemMergePreview")]
    [InlineData("PurgeConfirm")]
    [InlineData("TagNamePrompt")]
    public void Callback_is_detached_when_context_changes_and_when_window_closes(string name)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        session.Dispatch(() =>
        {
            (Window dialog, object original) = CreateDialog(name);
            (_, object replacement) = CreateDialog(name);
            dialog.DataContext = original;
            dialog.Show();
            try
            {
                Callback(original).Should().NotBeNull();
                dialog.DataContext = replacement;
                Callback(original).Should().BeNull("an old view model must not close the current dialog");
                Callback(replacement).Should().NotBeNull();
                dialog.DataContext = null;
                Callback(replacement).Should().BeNull();
                dialog.DataContext = replacement;
                dialog.Close();
                Callback(replacement).Should().BeNull("closed windows must release their callbacks");
            }
            finally
            {
                dialog.Close();
                (original as IDisposable)?.Dispose();
                (replacement as IDisposable)?.Dispose();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("confirm", ConfirmDialogResult.Confirm)]
    [InlineData("discard", ConfirmDialogResult.Discard)]
    [InlineData("cancel", ConfirmDialogResult.Cancel)]
    [InlineData("close", ConfirmDialogResult.Cancel)]
    public async Task Confirmation_returns_explicit_choices_and_cancels_title_bar_close(
        string action, ConfirmDialogResult expected)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            Window owner = new();
            ConfirmDialogViewModel vm = new("Confirm", "Proceed?", discardText: "Discard");
            ConfirmDialog dialog = new() { DataContext = vm };
            owner.Show();
            try
            {
                Task<ConfirmDialogResult> confirmation = dialog.ShowDialog<ConfirmDialogResult>(owner);
                switch (action)
                {
                    case "confirm": vm.ConfirmCommand.Execute(null); break;
                    case "discard": vm.DiscardCommand.Execute(null); break;
                    case "cancel": vm.CancelCommand.Execute(null); break;
                    default: dialog.Close(); break;
                }

                confirmation.IsCompleted.Should().BeTrue();
                (await confirmation).Should().Be(expected);
                vm.RequestClose.Should().BeNull();
            }
            finally
            {
                dialog.Close();
                owner.Close();
            }
        }, CancellationToken.None);
    }

    private static object? Callback(object vm)
    {
        return vm.GetType().GetProperty("RequestClose")!.GetValue(vm);
    }

    private static (Window Dialog, object ViewModel) CreateDialog(string name)
    {
        ItemMergePreview preview = new(ItemId.New(), ItemId.New(), "Source", "Target", [], [], [], 0);
        return name switch
        {
            "Confirm" => (new ConfirmDialog(), new ConfirmDialogViewModel("Confirm", "Message")),
            "BiblatexImportPreview" => (new BiblatexImportPreviewDialog(),
                new BiblatexImportPreviewDialogViewModel([], false, "Summary")),
            "ConflictResolution" => (new ConflictResolutionDialog(), new ConflictResolutionDialogViewModel()),
            "DuplicateItems" => (new DuplicateItemsDialog(),
                new DuplicateItemsDialogViewModel([], new Dictionary<ItemId, string>(), _ => Task.FromResult(false))),
            "ItemMergePreview" => (new ItemMergePreviewDialog(),
                new ItemMergePreviewDialogViewModel(preview,
                    (_, _, _) => Task.FromResult(Result<ItemMergePreview>.Success(preview)))),
            "PurgeConfirm" => (new PurgeConfirmDialog(), new PurgeConfirmDialogViewModel(["Item"], [])),
            "TagNamePrompt" => (new TagNamePromptDialog(), new TagNamePromptDialogViewModel("Tag", "Name", "Confirm")),
            _ => throw new ArgumentException("Unknown dialog", nameof(name))
        };
    }
}
