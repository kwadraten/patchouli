namespace Patchouli.Core.Pagination;

/// <summary>The forward resolution of one navigation position.</summary>
public sealed record ForwardResolution(
    int? PrintedNumber,
    string? Label,
    IReadOnlyList<MapDiagnostic> Diagnostics);

/// <summary>
/// Pure forward/reverse resolution over a <see cref="DocumentPageMap"/>.
/// <list type="bullet">
/// <item>forward: a position resolves to its printed number and display label;</item>
/// <item>reverse: a printed number resolves to ALL matching positions — the same printed
/// number may legitimately appear at multiple positions, there is no global
/// uniqueness;</item>
/// <item>no extrapolation happens outside rule ranges;</item>
/// <item>rules marked as needing repair are skipped (computation paused) with a diagnostic;
/// the rest of the map keeps resolving, so one bad reference never makes the whole map
/// unreadable.</item>
/// </list>
/// </summary>
public static class PageMapResolver
{
    public static ForwardResolution ResolveForward(
        DocumentPageMap map,
        IReadOnlyList<LogicalRuleHealth> ruleHealth,
        NavigationPosition position)
    {
        List<MapDiagnostic> diagnostics = [];

        if (position.PdfPage < 1)
        {
            return new ForwardResolution(null, null,
                [new MapDiagnostic("position_invalid", "Navigation positions use 1-based PDF pages.")]);
        }

        PhysicalPaginationRule? physical = map.PhysicalRules
            .FirstOrDefault(rule => position.PdfPage >= rule.StartPdfPage && position.PdfPage <= rule.EndPdfPage);
        int logicalIndex = -1;
        LogicalPaginationRule? logical = null;
        for (int i = 0; i < map.LogicalRules.Count; i++)
        {
            LogicalPaginationRule candidate = map.LogicalRules[i];
            if (position.PdfPage >= candidate.StartPdfPage && position.PdfPage <= candidate.EndPdfPage)
            {
                logical = candidate;
                logicalIndex = i;
                break;
            }
        }

        if (physical is not null && logical is not null)
        {
            diagnostics.Add(new MapDiagnostic("mixed_rule_kinds",
                $"PDF page {position.PdfPage} is covered by both a physical_page and a logical_page rule; " +
                "the map needs repair."));
            return new ForwardResolution(null, ResolveLabel(map, position, null), diagnostics);
        }

        int? printed = null;
        if (physical is not null)
        {
            printed = physical.PrintedStart + position.PdfPage - physical.StartPdfPage;
        }
        else if (logical is not null)
        {
            LogicalRuleHealth health = ruleHealth.Count > logicalIndex
                ? ruleHealth[logicalIndex]
                : LogicalRuleHealth.Healthy(logicalIndex);
            if (health.NeedsRepair)
            {
                diagnostics.Add(new MapDiagnostic("rule_paused",
                    "The logical_page rule covering this page needs repair; its printed numbers are " +
                    "currently unknown."));
            }
            else if (position.LogicalPage is null)
            {
                diagnostics.Add(new MapDiagnostic("logical_position_required",
                    $"PDF page {position.PdfPage} is inside a logical_page range; a printed number " +
                    "requires a logical-page position."));
            }
            else
            {
                int entryIndex = -1;
                for (int i = 0; i < logical.ReadingOrder.Count; i++)
                {
                    if (logical.ReadingOrder[i].BoxId == position.LogicalPage.Value)
                    {
                        entryIndex = i;
                        break;
                    }
                }

                if (entryIndex < 0)
                {
                    diagnostics.Add(new MapDiagnostic("logical_page_unknown",
                        $"Logical page {position.LogicalPage.Value} is not part of the rule's reading order."));
                }
                else
                {
                    printed = logical.PrintedStart + entryIndex;
                }
            }
        }
        else
        {
            diagnostics.Add(new MapDiagnostic("no_covering_rule",
                $"PDF page {position.PdfPage} is not covered by any pagination rule; printed numbers " +
                "are never extrapolated outside ranges."));
        }

        return new ForwardResolution(printed, ResolveLabel(map, position, printed), diagnostics);
    }

    public static IReadOnlyList<NavigationPosition> ResolveReverse(
        DocumentPageMap map,
        IReadOnlyList<LogicalRuleHealth> ruleHealth,
        int printedNumber)
    {
        List<NavigationPosition> positions = [];
        if (printedNumber < 1)
        {
            return positions;
        }

        foreach (PhysicalPaginationRule rule in map.PhysicalRules)
        {
            int pdfPage = rule.StartPdfPage + printedNumber - rule.PrintedStart;
            if (pdfPage >= rule.StartPdfPage && pdfPage <= rule.EndPdfPage)
            {
                positions.Add(new NavigationPosition(pdfPage));
            }
        }

        for (int i = 0; i < map.LogicalRules.Count; i++)
        {
            LogicalRuleHealth health = ruleHealth.Count > i ? ruleHealth[i] : LogicalRuleHealth.Healthy(i);
            if (health.NeedsRepair)
            {
                continue;
            }

            LogicalPaginationRule rule = map.LogicalRules[i];
            int entryIndex = printedNumber - rule.PrintedStart;
            if (entryIndex >= 0 && entryIndex < rule.ReadingOrder.Count)
            {
                LogicalPageReadingEntry entry = rule.ReadingOrder[entryIndex];
                positions.Add(new NavigationPosition(entry.PdfPage, entry.BoxId));
            }
        }

        return positions;
    }

    /// <summary>
    /// Resolves every chapter's display number at read time. A chapter whose target cannot be
    /// resolved keeps the chapter and degrades to its physical page with a diagnostic.
    /// </summary>
    public static IReadOnlyList<ResolvedChapterNode> ResolveChapters(
        DocumentPageMap map,
        IReadOnlyList<LogicalRuleHealth> ruleHealth)
    {
        return map.Chapters
            .Select(node => ResolveChapter(map, ruleHealth, node))
            .ToArray();
    }

    private static ResolvedChapterNode ResolveChapter(
        DocumentPageMap map,
        IReadOnlyList<LogicalRuleHealth> ruleHealth,
        ChapterNode node)
    {
        ForwardResolution forward = ResolveForward(map, ruleHealth, node.Target);
        List<MapDiagnostic> diagnostics = new(forward.Diagnostics);

        int? printed = forward.PrintedNumber;
        string? label = forward.Label;
        if (printed is null)
        {
            // Unknown printed number degrades to the physical page only; the chapter stays visible.
            diagnostics.Add(new MapDiagnostic("chapter_target_degraded",
                $"Chapter '{node.Title}' keeps its target but the printed number is unknown; " +
                $"showing PDF page {node.Target.PdfPage}."));
            label ??= $"PDF p.{node.Target.PdfPage}";
        }

        return new ResolvedChapterNode(
            node.ChapterId,
            node.Title,
            node.Target,
            printed,
            label,
            diagnostics,
            node.Children.Select(child => ResolveChapter(map, ruleHealth, child)).ToArray());
    }

    private static string? ResolveLabel(DocumentPageMap map, NavigationPosition position, int? printed)
    {
        // Explicit string labels override computed values for the same target; the more specific
        // logical-page target wins over the physical-page target.
        if (position.LogicalPage.HasValue)
        {
            PageLabelOverride? logicalOverride = map.PageLabels.FirstOrDefault(pageLabel =>
                pageLabel.Target.LogicalPage.HasValue &&
                pageLabel.Target.LogicalPage.Value == position.LogicalPage.Value);
            if (logicalOverride is not null)
            {
                return logicalOverride.Label;
            }
        }

        PageLabelOverride? physicalOverride = map.PageLabels.FirstOrDefault(pageLabel =>
            pageLabel.Target.PdfPage.HasValue && pageLabel.Target.PdfPage.Value == position.PdfPage);
        if (physicalOverride is not null)
        {
            return physicalOverride.Label;
        }

        return printed?.ToString();
    }
}
