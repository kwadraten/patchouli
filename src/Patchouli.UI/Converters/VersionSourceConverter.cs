using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Patchouli.Core.Documents;

namespace Patchouli.UI.Converters;

/// <summary>Maps a document revision/commit source (import, manual_edit, ocr_adopted,
/// migration, revert) to its Chinese display label or its accent brush for the
/// version-history table. Parameter selects the output: "Label" (default) or "Brush".</summary>
public sealed class VersionSourceConverter : IValueConverter
{
    private static readonly IBrush ImportBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush ManualEditBrush = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush OcrAdoptedBrush = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush MigrationBrush = new SolidColorBrush(Color.Parse("#9333EA"));
    private static readonly IBrush RevertBrush = new SolidColorBrush(Color.Parse("#D97706"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string source = value as string ?? string.Empty;
        return parameter as string == "Brush" ? BrushFor(source) : LabelFor(source);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static string LabelFor(string source)
    {
        return source switch
        {
            DocumentTreeRevisionSource.Import => "导入",
            DocumentTreeRevisionSource.ManualEdit => "手动编辑",
            DocumentTreeRevisionSource.OcrAdopted => "OCR 采纳",
            DocumentTreeRevisionSource.Migration => "迁移",
            DocumentTreeRevisionSource.Revert => "恢复",
            _ => source
        };
    }

    private static IBrush BrushFor(string source)
    {
        return source switch
        {
            DocumentTreeRevisionSource.ManualEdit => ManualEditBrush,
            DocumentTreeRevisionSource.OcrAdopted => OcrAdoptedBrush,
            DocumentTreeRevisionSource.Migration => MigrationBrush,
            DocumentTreeRevisionSource.Revert => RevertBrush,
            _ => ImportBrush
        };
    }
}
