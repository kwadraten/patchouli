using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Documents;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// The single translation-unit view of one page revision. Both the host job that hands pages to an
/// extension and the job that commits their results use it, so "translatable leaf box", "source text"
/// and "extension box" mean exactly the same thing on the read and the write side.
/// </summary>
internal sealed record LoadedTranslationPage(
    TranslationPageTarget Target,
    DocumentTreeRevision Revision,
    IReadOnlyList<DocumentBox> Boxes,
    string SourceText,
    PageTranslationStatus Status,
    IReadOnlySet<DocumentBoxId> TranslatableLeafBoxIds)
{
    /// <summary>The immutable identity an extension sees for this page revision.</summary>
    public ExtensionPageIdentity Identity => new(
        Revision.DocumentInstanceId, Target.PageId, Revision.TreeRevisionId, Target.PageIndex);

    /// <summary>The frozen source snapshot handed to the extension, including non-translatable context.</summary>
    public TranslationPageInput ToInput()
    {
        return new TranslationPageInput(Identity, Array.AsReadOnly(Boxes.Select(TranslationPageValidator.ToExtensionBox)
            .ToArray()));
    }

    /// <summary>True when every translatable leaf already has a current row in <paramref name="targetLanguage"/>.</summary>
    public bool IsCurrentIn(string targetLanguage)
    {
        return Status.IsCurrent &&
               Status.TargetLanguage is not null &&
               string.Equals(Status.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase) &&
               !Status.StaleBoxIds.Any(TranslatableLeafBoxIds.Contains);
    }
}

/// <summary>
/// Host-side validation and payload projection for translation pages. It contains no scheduling and no
/// persistence: it decides which boxes a page may translate and turns validated extension outcomes into
/// the typed payload replacements that one atomic page write expects.
/// </summary>
internal static class TranslationPageValidator
{
    /// <summary>
    /// Box types whose payload text is translated. Code, equations, images, charts, media, logical pages,
    /// page numbers and suppressed boxes are context only and are never submitted as replacements.
    /// </summary>
    private static readonly HashSet<string> TranslatableBoxTypes = new(StringComparer.Ordinal)
    {
        DocumentBoxType.Text,
        DocumentBoxType.Title,
        DocumentBoxType.RefText,
        DocumentBoxType.List,
        DocumentBoxType.Table,
        DocumentBoxType.Algorithm,
        DocumentBoxType.Index,
        DocumentBoxType.Header,
        DocumentBoxType.Footer,
        DocumentBoxType.AsideText,
        DocumentBoxType.PageFootnote,
        DocumentBoxType.ImageCaption,
        DocumentBoxType.ImageFootnote,
        DocumentBoxType.TableCaption,
        DocumentBoxType.TableFootnote,
        DocumentBoxType.ChartCaption,
        DocumentBoxType.ChartFootnote,
        DocumentBoxType.CodeCaption,
        DocumentBoxType.CodeFootnote
    };

    /// <summary>Projects one loaded revision into the page view shared by reads and commits.</summary>
    public static LoadedTranslationPage BuildLoadedPage(
        TranslationPageTarget target,
        DocumentTreeRevision revision,
        IReadOnlyList<DocumentBox> boxes,
        PageTranslationStatus status)
    {
        HashSet<DocumentBoxId> parentBoxIds = boxes
            .Where(static box => box.ParentBoxId is not null)
            .Select(static box => box.ParentBoxId!.Value)
            .ToHashSet();
        HashSet<DocumentBoxId> translatableLeafBoxIds = [];
        List<string> referenceTexts = [];
        foreach (DocumentBox box in boxes)
        {
            string text = ReadBoxText(box);
            if (IsTranslatableLeaf(box, parentBoxIds, text))
            {
                translatableLeafBoxIds.Add(box.BoxId);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                referenceTexts.Add(text);
            }
        }

        return new LoadedTranslationPage(
            target,
            revision,
            boxes,
            string.Join("\n\n", referenceTexts),
            status,
            translatableLeafBoxIds);
    }

    /// <summary>
    /// A box contributes a translation only when it is a leaf (no other box names it as its parent), is
    /// not suppressed, has a translatable type and carries non-empty text. Containers such as
    /// <see cref="DocumentBoxType.LogicalPage"/> and every code/equation/image/chart/media box are
    /// therefore excluded from both segmentation and commit.
    /// </summary>
    private static bool IsTranslatableLeaf(
        DocumentBox box,
        IReadOnlySet<DocumentBoxId> parentBoxIds,
        string text)
    {
        return !box.Suppressed &&
               !parentBoxIds.Contains(box.BoxId) &&
               TranslatableBoxTypes.Contains(box.BoxType) &&
               !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>
    /// Extracts the text a box contributes as source material. Non-translatable payloads (code, equations,
    /// media descriptions) still contribute reference text, but suppressed boxes and logical pages do not.
    /// </summary>
    public static string ReadBoxText(DocumentBox box)
    {
        if (box.Suppressed || box.BoxType == DocumentBoxType.LogicalPage)
        {
            return string.Empty;
        }

        return box.Payload switch
        {
            TextBoxPayload textBox => textBox.Markdown ?? string.Empty,
            ListBoxPayload listBox => listBox.Markdown ?? string.Empty,
            TableBoxPayload tableBox => TableContentAdapter.TryGetHtml(tableBox, out string html)
                ? html
                : tableBox.Markdown,
            AlgorithmBoxPayload algorithmBox => algorithmBox.Markdown ?? string.Empty,
            CodeBoxPayload codeBox => codeBox.Code ?? string.Empty,
            EquationBoxPayload equationBox => equationBox.Latex ?? string.Empty,
            MediaBoxPayload mediaBox => mediaBox.Description ?? string.Empty,
            _ => string.Empty
        };
    }

    /// <summary>Snapshots one source box as the immutable reference view an extension receives.</summary>
    public static ExtensionBox ToExtensionBox(DocumentBox box)
    {
        return new ExtensionBox(
            box.BoxId,
            box.ParentBoxId,
            box.BoxType,
            box.SubType,
            ReadBoxText(box),
            DocumentBoxPayloadSerializer.Serialize(box.Payload) ?? "null",
            box.BBox,
            box.HeadingLevel,
            box.CodeLanguage,
            box.Suppressed)
        {
            Payload = SnapshotPayload(box.Payload)
        };
    }

    private static DocumentBoxPayload? SnapshotPayload(DocumentBoxPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        IReadOnlyList<DocumentAnnotation>? annotations = payload.Annotations is { } values
            ? Array.AsReadOnly(values.ToArray())
            : null;
        return payload switch
        {
            TextBoxPayload text => text with { Annotations = annotations },
            EquationBoxPayload equation => equation with { Annotations = annotations },
            ListBoxPayload list => list with { Annotations = annotations },
            TableBoxPayload table => table with { Annotations = annotations },
            AlgorithmBoxPayload algorithm => algorithm with { Annotations = annotations },
            CodeBoxPayload code => code with { Annotations = annotations },
            MediaBoxPayload media => media with { Annotations = annotations },
            _ => null
        };
    }

    /// <summary>
    /// Validates one page's extension outcomes against the frozen page and builds the payload replacements
    /// for its single atomic write. A structural violation (unknown or duplicate box id, malformed payload)
    /// reports <paramref name="failureCode"/> and yields no replacements; a well-formed outcome set reports
    /// the first box failure and any skips for the page's committed/partial/skipped decision.
    /// </summary>
    public static bool TryBuildReplacements(
        LoadedTranslationPage page,
        IReadOnlyList<ExtensionBoxOutcome>? outcomes,
        out Dictionary<DocumentBoxId, DocumentBoxPayload> replacements,
        out PageOutcomeSummary summary)
    {
        replacements = [];
        summary = default;
        if (outcomes is null)
        {
            summary = PageOutcomeSummary.Invalid(TranslationErrorCodes.BatchMismatch,
                "The translation extension returned no outcome list.");
            return false;
        }

        Dictionary<DocumentBoxId, ExtensionBoxOutcome> byId = [];
        foreach (ExtensionBoxOutcome outcome in outcomes)
        {
            if (outcome is null || !byId.TryAdd(outcome.BoxId, outcome))
            {
                summary = PageOutcomeSummary.Invalid(TranslationErrorCodes.BatchMismatch,
                    "The translation extension returned an empty or duplicate box id.");
                return false;
            }

            if (!page.TranslatableLeafBoxIds.Contains(outcome.BoxId))
            {
                summary = PageOutcomeSummary.Invalid(TranslationErrorCodes.BatchMismatch,
                    $"The translation extension returned an unknown or non-translatable box id '{outcome.BoxId}'.");
                return false;
            }
        }

        if (byId.Count != page.TranslatableLeafBoxIds.Count ||
            page.TranslatableLeafBoxIds.Any(boxId => !byId.ContainsKey(boxId)))
        {
            summary = PageOutcomeSummary.Invalid(TranslationErrorCodes.BatchMismatch,
                "The translation extension omitted one or more eligible box outcomes.");
            return false;
        }

        string? firstFailureCode = null;
        string? firstFailureMessage = null;
        bool hasFailures = false;
        bool hasSkipped = false;
        foreach (DocumentBox box in page.Boxes)
        {
            if (!page.TranslatableLeafBoxIds.Contains(box.BoxId))
            {
                continue;
            }

            ExtensionBoxOutcome outcome = byId[box.BoxId];

            switch (outcome.Kind)
            {
                case ExtensionBoxOutcomeKind.Failed:
                    hasFailures = true;
                    firstFailureCode ??= outcome.ErrorCode ?? AppErrorCodes.InvalidState;
                    firstFailureMessage ??=
                        outcome.ErrorMessage ?? $"The translation extension failed to translate box '{box.BoxId}'.";
                    continue;
                case ExtensionBoxOutcomeKind.Skipped:
                    hasSkipped = true;
                    continue;
                case ExtensionBoxOutcomeKind.Translated:
                    break;
                default:
                    summary = PageOutcomeSummary.Invalid(TranslationErrorCodes.BatchMismatch,
                        $"The translation extension returned an unsupported outcome for box '{box.BoxId}'.");
                    return false;
            }

            DocumentBoxPayload? translatedPayload = CreateExtensionTranslatedPayload(box, outcome);
            if (translatedPayload is null)
            {
                summary = PageOutcomeSummary.Invalid(TranslationErrorCodes.BatchMismatch,
                    $"The translation extension returned a malformed translation for box '{box.BoxId}'.");
                return false;
            }

            replacements[box.BoxId] = translatedPayload;
        }

        summary = new PageOutcomeSummary(hasFailures, hasSkipped, firstFailureCode, firstFailureMessage);
        return true;
    }

    private static DocumentBoxPayload? CreateExtensionTranslatedPayload(
        DocumentBox source,
        ExtensionBoxOutcome outcome)
    {
        DocumentBoxPayload? original = source.Payload;
        bool hasJsonPayload = !string.IsNullOrWhiteSpace(outcome.TranslatedPayloadJson);
        if (outcome.TranslatedPayload is { } typedPayload)
        {
            if (hasJsonPayload || outcome.TranslatedText is not null)
            {
                return null;
            }

            return ValidateExtensionPayload(original, SnapshotPayload(typedPayload));
        }

        if (hasJsonPayload && outcome.TranslatedText is not null)
        {
            return null;
        }

        if (outcome.TranslatedPayloadJson is { Length: > 0 } payloadJson)
        {
            DocumentBoxPayload? candidate = DocumentBoxPayloadSerializer.Deserialize(
                source.BoxType, source.SubType, payloadJson);
            return ValidateExtensionPayload(original, SnapshotPayload(candidate));
        }

        return outcome.TranslatedText is { } translatedContent && !string.IsNullOrWhiteSpace(translatedContent)
            ? CreateTranslatedPayload(original, translatedContent)
            : null;
    }

    private static DocumentBoxPayload? CreateTranslatedPayload(
        DocumentBoxPayload? payload,
        string translated)
    {
        return payload switch
        {
            TextBoxPayload textBox => new TextBoxPayload(translated)
            {
                Annotations = textBox.Annotations
            },
            ListBoxPayload listBox => new ListBoxPayload(translated)
            {
                Annotations = listBox.Annotations
            },
            TableBoxPayload tableBox => new TableBoxPayload(translated,
                translated.Trim() == "[Table]" ? tableBox.Html : null)
            {
                Annotations = tableBox.Annotations
            },
            AlgorithmBoxPayload algorithmBox => new AlgorithmBoxPayload(translated)
            {
                Annotations = algorithmBox.Annotations
            },
            _ => null
        };
    }

    private static DocumentBoxPayload? ValidateExtensionPayload(
        DocumentBoxPayload? original,
        DocumentBoxPayload? candidate)
    {
        if (candidate is null || original is null || candidate.GetType() != original.GetType())
        {
            return null;
        }

        string? candidateText = candidate switch
        {
            TextBoxPayload text => text.Markdown,
            ListBoxPayload list => list.Markdown,
            TableBoxPayload table => TableContentAdapter.TryGetHtml(table, out string html) ? html : table.Markdown,
            AlgorithmBoxPayload algorithm => algorithm.Markdown,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(candidateText))
        {
            return null;
        }

        if (original is TableBoxPayload sourceTable && candidate is TableBoxPayload translatedTable &&
            (TableContentAdapter.TryGetHtml(sourceTable, out _) ||
             TableContentAdapter.TryGetHtml(translatedTable, out _)))
        {
            Result<TableBoxPayload> tableValidation =
                TableContentAdapter.ValidateTranslation(sourceTable, translatedTable);
            return tableValidation.IsSuccess
                ? tableValidation.Value with { Annotations = SnapshotAnnotations(sourceTable.Annotations) }
                : null;
        }

        return (original, candidate) switch
        {
            (TextBoxPayload sourceText, TextBoxPayload translatedText) => translatedText with
            {
                Annotations = SnapshotAnnotations(sourceText.Annotations)
            },
            (ListBoxPayload sourceList, ListBoxPayload translatedList) => translatedList with
            {
                Annotations = SnapshotAnnotations(sourceList.Annotations)
            },
            (TableBoxPayload sourceLegacyTable, TableBoxPayload translatedLegacyTable) => translatedLegacyTable with
            {
                Annotations = SnapshotAnnotations(sourceLegacyTable.Annotations)
            },
            (AlgorithmBoxPayload sourceAlgorithm, AlgorithmBoxPayload translatedAlgorithm) => translatedAlgorithm
                with
                {
                    Annotations = SnapshotAnnotations(sourceAlgorithm.Annotations)
                },
            _ => null
        };
    }

    private static IReadOnlyList<DocumentAnnotation>? SnapshotAnnotations(
        IReadOnlyList<DocumentAnnotation>? annotations)
    {
        return annotations is null ? null : Array.AsReadOnly(annotations.ToArray());
    }
}

/// <summary>
/// The validated shape of one page's extension outcomes. <see cref="Structural"/> is true when the outcome
/// list itself is invalid (as opposed to a page whose boxes merely failed or were skipped).
/// </summary>
internal readonly record struct PageOutcomeSummary(
    bool HasFailures,
    bool HasSkipped,
    string? FirstFailureCode,
    string? FirstFailureMessage,
    bool Structural = false)
{
    /// <summary>An outcome list that is rejected as a whole instead of being committed.</summary>
    public static PageOutcomeSummary Invalid(string errorCode, string errorMessage)
    {
        return new PageOutcomeSummary(false, false, errorCode, errorMessage, true);
    }
}
