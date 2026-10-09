using Avalonia.Controls;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class PurgeConfirmDialogTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Closing_dialog_completes_confirmation_with_the_expected_result(bool? expected)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
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
        }, CancellationToken.None);
    }
}
