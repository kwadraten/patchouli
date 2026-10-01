using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace Patchouli.Reading;

// Accessibility peer: exposes the read-only reading surface to screen readers as a text control
// whose value is the full plain text of its columns. Avalonia's public automation model has no
// ITextProvider (caret/range/attribute navigation), so IValueProvider is the ceiling here — it
// lets assistive tech read the content. Adapted from AvaloniaRichEditor's RichEditorAutomationPeer
// (MIT, Copyright (c) 2026 centwon; see THIRD-PARTY-NOTICES.md).
internal sealed class ReadingAutomationPeer : ControlAutomationPeer, IValueProvider
{
    private readonly ReadingView _owner;

    public ReadingAutomationPeer(ReadingView owner) : base(owner)
    {
        _owner = owner;
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
    {
        return AutomationControlType.Text;
    }

    protected override string GetClassNameCore()
    {
        return nameof(ReadingView);
    }

    // Falls back to a sensible default when the host hasn't set AutomationProperties.Name.
    protected override string? GetNameCore()
    {
        string? name = base.GetNameCore();
        return string.IsNullOrEmpty(name) ? "阅读内容" : name;
    }

    protected override bool IsContentElementCore()
    {
        return true;
    }

    protected override bool IsControlElementCore()
    {
        return true;
    }

    // IValueProvider: the surface is read-only, so SetValue is intentionally a no-op.
    public bool IsReadOnly => true;

    public string? Value => _owner.GetPlainText();

    public void SetValue(string? value)
    {
    }
}
