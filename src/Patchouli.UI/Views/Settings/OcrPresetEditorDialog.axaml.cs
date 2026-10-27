using Avalonia.Controls;

namespace Patchouli.UI.Views.Settings;

/// <summary>
///     Retired modal preset editor — composition is inline rows on the OCR 预设编排 settings tab
///     and no code path constructs this dialog. The stub remains only so the compiled view type
///     stays consistent until the file is physically removed during cleanup.
/// </summary>
public partial class OcrPresetEditorDialog : Window
{
    public OcrPresetEditorDialog()
    {
        InitializeComponent();
    }
}
