using Patchouli.Core.Ids;

namespace Patchouli.Core.Translation;

/// <summary>
/// One translated segment. <see cref="BoxId"/> and <see cref="PartIndex"/> must be copied from the
/// requested <see cref="TranslationSegment"/>; they are never inferred from response order.
/// </summary>
public sealed record TranslationSegmentTranslation(
    string SegmentId,
    DocumentBoxId BoxId,
    int PartIndex,
    string TranslatedText);

/// <summary>Raw provider response and request diagnostics retained for error reporting and retries.</summary>
public sealed record TranslationResponseDiagnostics(
    string? RawResponse = null,
    string? ProviderId = null,
    string? Model = null,
    string? FinishReason = null,
    TimeSpan? Duration = null,
    int? InputTokens = null,
    int? OutputTokens = null);

/// <summary>
/// The validated result of one model request. <see cref="RequestedSegmentIds"/> echoes the exact
/// requested id set in source order, so <see cref="ITranslationResultProcessor"/> can detect missing
/// segments without consulting transient batch state. <see cref="Translations"/> must contain one
/// entry per requested id; duplicate, unknown or missing ids make the request fail.
/// </summary>
public sealed record TranslationBatchResult(
    int BatchIndex,
    IReadOnlyList<string> RequestedSegmentIds,
    IReadOnlyList<TranslationSegmentTranslation> Translations,
    TranslationResponseDiagnostics? Diagnostics = null);
