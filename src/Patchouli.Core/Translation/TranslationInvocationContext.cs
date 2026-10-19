namespace Patchouli.Core.Translation;

/// <summary>A source-to-target term supplied to the model as terminology guidance.</summary>
public sealed record TranslationGlossaryEntry(
    string SourceTerm,
    string TargetTerm,
    string? Note = null);

/// <summary>Role of a non-translatable reference text supplied for coherence.</summary>
public enum TranslationReferenceKind
{
    PreviousPageSource,
    NextPageSource,
    PreviousWindowTranslation
}

/// <summary>
/// Reference text supplied alongside a batch. It is context only: implementations must never
/// translate it, count it as part of the batch, or return a translation for it.
/// </summary>
public sealed record TranslationReferenceContext(
    TranslationReferenceKind Kind,
    string Text,
    string? Label = null);

/// <summary>
/// Per-request context for one batch. Only the segments in the batch are translatable;
/// <see cref="ReferenceContexts"/> are explicitly reference material. A null
/// <see cref="ProviderId"/> or <see cref="Model"/> means the host default selection.
/// <see cref="Timeout"/> is the budget for this request and must be honored by the invoker.
/// </summary>
public sealed record TranslationInvocationContext(
    string TargetLanguage,
    TimeSpan Timeout,
    string? DocumentTitle = null,
    IReadOnlyList<TranslationGlossaryEntry>? Glossary = null,
    IReadOnlyList<TranslationReferenceContext>? ReferenceContexts = null,
    string? ProviderId = null,
    string? Model = null);
