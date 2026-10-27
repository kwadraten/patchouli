using System.Globalization;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr;

/// <summary>
/// Projects page/input progress from one extension invocation onto its queue attempt. Page indices
/// come only from the frozen job selection; extension messages never become public diagnostics.
/// Closing the sink prevents retained callbacks from overwriting import/adoption progress.
/// </summary>
internal sealed class OcrExtensionProgress : IProgress<ExtensionProgress>, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, PageProgress> _pages;
    private readonly int _totalPages;
    private readonly int _resolvedPages;
    private readonly IProgress<OcrTaskStageProgress>? _progress;
    private double _pageFractions;
    private int _settledPages;
    private bool _closed;

    public OcrExtensionProgress(IReadOnlyList<OcrJobPage> pages, int totalPages,
        IProgress<OcrTaskStageProgress>? progress)
    {
        _pages = pages.ToDictionary(page => page.Page.PageIndex, page => new PageProgress(page));
        _totalPages = totalPages;
        _resolvedPages = totalPages - pages.Count;
        _progress = progress;
    }

    public void Report(ExtensionProgress value)
    {
        lock (_gate)
        {
            if (_closed || _progress is null ||
                !int.TryParse(value.ItemId, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                !_pages.TryGetValue(index, out PageProgress? page) || page.Settled)
            {
                return;
            }

            string? phase = null;
            double previousFraction = page.Fraction;
            int completed = Math.Clamp(value.Completed, 0, Math.Max(0, value.Total));
            double fraction = value.Total > 0 ? completed / (double)value.Total : 0;
            if (value.Phase == "page-ocr")
            {
                page.CompletedInputs = Math.Max(page.CompletedInputs,
                    Math.Min(completed, page.Page.Inputs.Count));
                if (value.Status is ExtensionProgressStatus.Completed or ExtensionProgressStatus.Failed
                    or ExtensionProgressStatus.Cancelled)
                {
                    page.Settled = true;
                    _settledPages++;
                    fraction = 1;
                }

                page.Fraction = Math.Max(page.Fraction, fraction);
            }
            else if (value.ExecutionId is { } inputId &&
                     page.InputFractions.TryGetValue(inputId, out double previousInputFraction))
            {
                double inputFraction;
                switch (value.Phase)
                {
                    case "layout-detection":
                        inputFraction = 0.1 * fraction;
                        break;
                    case "reading-order":
                        inputFraction = 0.1 + 0.1 * fraction;
                        break;
                    case "region-ocr":
                        inputFraction = 0.2 + 0.8 * fraction;
                        break;
                    case "direct-ocr":
                        inputFraction = fraction;
                        break;
                    default:
                        return;
                }

                phase = value.Phase;
                inputFraction = Math.Max(previousInputFraction, inputFraction);
                page.InputFractions[inputId] = inputFraction;
                page.TotalInputFraction += inputFraction - previousInputFraction;
                page.Fraction = Math.Max(page.Fraction,
                    page.TotalInputFraction / page.Page.Inputs.Count);
            }
            else
            {
                return;
            }

            _pageFractions += page.Fraction - previousFraction;
            int settled = _resolvedPages + _settledPages;
            string detail = FormattableString.Invariant(
                $"pages:{settled}/{_totalPages};page:{index + 1};inputs:{page.CompletedInputs}/{page.Page.Inputs.Count}");
            if (phase is not null)
            {
                detail += FormattableString.Invariant($";phase:{phase};units:{completed}/{Math.Max(0, value.Total)}");
            }

            // Serialize publication with closure and other reports; no delayed Progress<T> posts
            // may arrive out of order after the image phase has already returned.
            _progress.Report(new OcrTaskStageProgress(OcrTaskStage.Recognizing,
                _totalPages == 0 ? 0 : Math.Clamp((_resolvedPages + _pageFractions) / _totalPages, 0, 1),
                detail));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    private sealed class PageProgress(OcrJobPage page)
    {
        public OcrJobPage Page { get; } = page;

        public Dictionary<string, double> InputFractions { get; } = page.Inputs
            .ToDictionary(input => input.Id.Value, _ => 0.0, StringComparer.Ordinal);

        public double TotalInputFraction { get; set; }

        public int CompletedInputs { get; set; }
        public double Fraction { get; set; }
        public bool Settled { get; set; }
    }
}
