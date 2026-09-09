using Patchouli.Ocr;

namespace Patchouli.Host.Import;

/// <summary>A library item (or filtered row) eligible for OCR enqueue, in UI-agnostic form.</summary>
public sealed record OcrEnqueueItem(
    string Title,
    string? DocumentInstanceId,
    string? SourcePath);

/// <summary>A per-item OCR enqueue failure with the item title and reason.</summary>
public sealed record OcrEnqueueFailure(
    string Title,
    string Message);

/// <summary>Aggregate outcome of an OCR enqueue pass.</summary>
public sealed record OcrEnqueueSummary(
    int Succeeded,
    int Failed,
    int Skipped,
    IReadOnlyList<OcrEnqueueFailure> Failures,
    IOcrQueueScheduler? Queue);
