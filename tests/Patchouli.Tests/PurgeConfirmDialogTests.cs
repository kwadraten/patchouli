using Avalonia.Controls;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class PurgeConfirmDialogTests
{
    [Fact]
    public void Large_confirmation_keeps_full_dependency_totals_but_bounds_both_visual_previews()
    {
        string[] titles = Enumerable.Range(0, 10000).Select(index => $"Item {index}").ToArray();
        ItemPurgeDependencyReport[] reports = titles.Select((_, index) => new ItemPurgeDependencyReport(
            ItemId.New(), ["shard"], 1, false, index == 9999, index == 9999)).ToArray();
        using PurgeConfirmDialogViewModel dialog = new(titles, reports);

        dialog.Titles.Should().HaveCount(10000);
        dialog.Reports.Should().HaveCount(10000);
        dialog.DisplayedTitles.Should().HaveCount(PurgeConfirmDialogViewModel.PreviewLimit);
        dialog.DisplayedReports.Should().HaveCount(PurgeConfirmDialogViewModel.PreviewLimit);
        dialog.TotalSnapshotCount.Should().Be(10000);
        dialog.AnyOcrCandidates.Should().BeTrue("dependencies outside the preview still participate");
        dialog.AnyWorking.Should().BeTrue();
        dialog.PreviewLimitMessage.Should().Contain("10000");
        titles[0] = "caller changed its selection";
        dialog.Titles[0].Should().Be("Item 0");
        string xaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PurgeConfirmDialog.axaml"));
        xaml.Should().Contain("ItemsSource=\"{Binding DisplayedTitles}\"")
            .And.Contain("ItemsSource=\"{Binding DisplayedReports}\"")
            .And.NotContain("ItemsSource=\"{Binding Titles}\"")
            .And.NotContain("ItemsSource=\"{Binding Reports}\"");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Closing_dialog_completes_confirmation_with_the_expected_result(bool? expected)
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            Window owner = new();
            using PurgeConfirmDialogViewModel vm = new(["Item to purge"], []);
            PurgeConfirmDialog dialog = new() { DataContext = vm };
            owner.Show();
            try
            {
                Task<bool?> confirmation = dialog.ShowDialog<bool?>(owner);
                confirmation.IsCompleted.Should().BeFalse();

                if (expected == true)
                {
                    vm.ConfirmCommand.Execute(null);
                }
                else if (expected == false)
                {
                    vm.CancelCommand.Execute(null);
                }
                else
                {
                    dialog.Close();
                }

                confirmation.IsCompleted.Should().BeTrue(
                    "the window must return a result so the purge command can finish waiting for confirmation");
                (await confirmation).Should().Be(expected);
            }
            finally
            {
                dialog.Close();
                owner.Close();
            }

            return true;
        }, CancellationToken.None);
    }
}
