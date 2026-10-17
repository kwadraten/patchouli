using Avalonia.Headless;
using FluentAssertions;
using Patchouli.UI.Controls;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class PathPickerTextBoxTests
{
    [Fact]
    public async Task Independent_ui_sessions_can_read_folder_and_file_icon_bounds()
    {
        for (int iteration = 0; iteration < 2; iteration++)
        {
            await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
            await session.Dispatch(() =>
            {
                PathPickerTextBox picker = new() { PickerMode = PathPickerMode.Folder };
                picker.IconData!.Bounds.Width.Should().BeGreaterThan(0);
                picker.PickerMode = PathPickerMode.OpenFile;
                picker.IconData!.Bounds.Width.Should().BeGreaterThan(0);
            }, CancellationToken.None);
        }
    }
}
