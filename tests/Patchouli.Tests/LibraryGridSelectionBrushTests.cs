using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.UI.Views;
using Xunit;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public class LibraryGridSelectionBrushTests
{
    [Fact]
    public async Task LibraryGrid_selected_row_uses_tertiary_brush()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(UI.App));
        await session.Dispatch(() =>
        {
            Window window = new()
            {
                Width = 800,
                Height = 600,
                Content = new LibraryPage()
            };
            window.Show();
            try
            {
                LibraryPage page = (LibraryPage)window.Content!;
                DataGrid grid = page.FindControl<DataGrid>("LibraryGrid")!;
                grid.ItemsSource = new[] { new { Title = "Row A" }, new { Title = "Row B" } };

                window.Measure(new Size(800, 600));
                window.Arrange(new Rect(0, 0, 800, 600));

                grid.SelectedIndex = 0;
                grid.UpdateLayout();

                // The DataGrid theme paints selection via this template rectangle, not the row Background.
                DataGridRow selectedRow = grid.GetVisualDescendants().OfType<DataGridRow>()
                    .First(row => row.IsSelected);
                Rectangle background = selectedRow.GetVisualDescendants().OfType<Rectangle>()
                    .First(child => child.Name == "BackgroundRectangle");

                Color expected = ((ISolidColorBrush)Application.Current!.FindResource("SelectionBrush")!).Color;
                ISolidColorBrush? fill = background.Fill as ISolidColorBrush;
                fill.Should().NotBeNull();
                fill!.Color.Should().Be(expected);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }
}
