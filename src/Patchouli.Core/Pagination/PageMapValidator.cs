using Patchouli.Core.Ids;

namespace Patchouli.Core.Pagination;

/// <summary>A single shape-level validation failure of a page map definition.</summary>
public sealed record PageMapValidationError(string Code, string Message);

/// <summary>
/// Pure, storage-free validation of a <see cref="DocumentPageMap"/> definition.
/// Write-time checks that need the database (document/page ownership, logical-page Box type
/// and current committed revision ownership, range bounds against the real page count) live
/// in the infrastructure page-map service, not here.
/// </summary>
public static class PageMapValidator
{
    public const int MaxChapterDepth = 16;
    public const int MaxChapterNodes = 2000;
    public const int MaxChildrenPerNode = 500;
    public const int MaxRulesPerKind = 256;
    public const int MaxReadingEntriesPerRule = 20000;
    public const int MaxLabelOverrides = 5000;
    public const int MaxChapterIdLength = 128;
    public const int MaxTitleLength = 500;
    public const int MaxLabelLength = 200;

    public static IReadOnlyList<PageMapValidationError> Validate(DocumentPageMap map)
    {
        List<PageMapValidationError> errors = [];

        ValidatePhysicalRules(map, errors);
        ValidateLogicalRules(map, errors);
        ValidateNoMixedRules(map, errors);
        ValidateLabelOverrides(map, errors);
        ValidateChapters(map, errors);

        return errors;
    }

    private static void ValidatePhysicalRules(DocumentPageMap map, List<PageMapValidationError> errors)
    {
        if (map.PhysicalRules.Count > MaxRulesPerKind)
        {
            errors.Add(new PageMapValidationError("rule_count",
                $"Too many physical_page rules ({map.PhysicalRules.Count}); the limit is {MaxRulesPerKind}."));
        }

        for (int i = 0; i < map.PhysicalRules.Count; i++)
        {
            PhysicalPaginationRule rule = map.PhysicalRules[i];
            string prefix = $"pagination.rules[{i}] (physical_page)";
            if (rule.StartPdfPage < 1)
            {
                errors.Add(new PageMapValidationError("range_start",
                    $"{prefix}: start must be a 1-based PDF page, got {rule.StartPdfPage}."));
            }

            if (rule.EndPdfPage < rule.StartPdfPage)
            {
                errors.Add(new PageMapValidationError("range_order",
                    $"{prefix}: end ({rule.EndPdfPage}) must not be before start ({rule.StartPdfPage})."));
            }

            if (rule.PrintedStart < 1)
            {
                errors.Add(new PageMapValidationError("printed_start",
                    $"{prefix}: printed_start must be a positive integer, got {rule.PrintedStart}."));
            }
        }
    }

    private static void ValidateLogicalRules(DocumentPageMap map, List<PageMapValidationError> errors)
    {
        if (map.LogicalRules.Count > MaxRulesPerKind)
        {
            errors.Add(new PageMapValidationError("rule_count",
                $"Too many logical_page rules ({map.LogicalRules.Count}); the limit is {MaxRulesPerKind}."));
        }

        for (int i = 0; i < map.LogicalRules.Count; i++)
        {
            LogicalPaginationRule rule = map.LogicalRules[i];
            string prefix = $"pagination.rules[{i}] (logical_page)";
            if (rule.StartPdfPage < 1)
            {
                errors.Add(new PageMapValidationError("range_start",
                    $"{prefix}: start must be a 1-based PDF page, got {rule.StartPdfPage}."));
            }

            if (rule.EndPdfPage < rule.StartPdfPage)
            {
                errors.Add(new PageMapValidationError("range_order",
                    $"{prefix}: end ({rule.EndPdfPage}) must not be before start ({rule.StartPdfPage})."));
            }

            if (rule.PrintedStart < 1)
            {
                errors.Add(new PageMapValidationError("printed_start",
                    $"{prefix}: printed_start must be a positive integer, got {rule.PrintedStart}."));
            }

            if (rule.ReadingOrder.Count == 0)
            {
                errors.Add(new PageMapValidationError("reading_order_empty",
                    $"{prefix}: reading_order must list at least one logical page."));
                continue;
            }

            if (rule.ReadingOrder.Count > MaxReadingEntriesPerRule)
            {
                errors.Add(new PageMapValidationError("reading_order_count",
                    $"{prefix}: too many reading_order entries ({rule.ReadingOrder.Count}); " +
                    $"the limit is {MaxReadingEntriesPerRule}."));
            }

            HashSet<DocumentBoxId> seen = [];
            HashSet<int> coveredPages = [];
            foreach (LogicalPageReadingEntry entry in rule.ReadingOrder)
            {
                if (entry.PdfPage < rule.StartPdfPage || entry.PdfPage > rule.EndPdfPage)
                {
                    errors.Add(new PageMapValidationError("reading_order_range",
                        $"{prefix}: logical page {entry.BoxId} targets PDF page {entry.PdfPage}, " +
                        $"which is outside the rule range [{rule.StartPdfPage}, {rule.EndPdfPage}]."));
                }
                else
                {
                    coveredPages.Add(entry.PdfPage);
                }

                if (!seen.Add(entry.BoxId))
                {
                    errors.Add(new PageMapValidationError("reading_order_duplicate",
                        $"{prefix}: logical page {entry.BoxId} appears more than once in reading_order."));
                }
            }

            // A physical page with no logical pages inside a logical_page range must not count
            // as one: require a re-split of the range or a complete logical division.
            for (int page = rule.StartPdfPage; page <= rule.EndPdfPage; page++)
            {
                if (!coveredPages.Contains(page))
                {
                    errors.Add(new PageMapValidationError("logical_range_gap",
                        $"{prefix}: PDF page {page} has no logical pages in reading_order; split the range " +
                        "or provide a complete logical division."));
                }
            }
        }
    }

    private static void ValidateNoMixedRules(DocumentPageMap map, List<PageMapValidationError> errors)
    {
        foreach (LogicalPaginationRule logical in map.LogicalRules)
        {
            foreach (PhysicalPaginationRule physical in map.PhysicalRules)
            {
                int overlapStart = Math.Max(logical.StartPdfPage, physical.StartPdfPage);
                int overlapEnd = Math.Min(logical.EndPdfPage, physical.EndPdfPage);
                if (overlapStart <= overlapEnd)
                {
                    errors.Add(new PageMapValidationError("mixed_rule_kinds",
                        $"Physical pages [{overlapStart}, {overlapEnd}] are covered by both a physical_page " +
                        "rule and a logical_page rule; split the ranges so each physical page has exactly " +
                        "one rule kind."));
                }
            }
        }
    }

    private static void ValidateLabelOverrides(DocumentPageMap map, List<PageMapValidationError> errors)
    {
        if (map.PageLabels.Count > MaxLabelOverrides)
        {
            errors.Add(new PageMapValidationError("label_count",
                $"Too many page_labels ({map.PageLabels.Count}); the limit is {MaxLabelOverrides}."));
        }

        HashSet<string> seenTargets = new(StringComparer.Ordinal);
        for (int i = 0; i < map.PageLabels.Count; i++)
        {
            PageLabelOverride pageLabel = map.PageLabels[i];
            string prefix = $"pagination.page_labels[{i}]";
            bool hasPdfPage = pageLabel.Target.PdfPage.HasValue;
            bool hasLogicalPage = pageLabel.Target.LogicalPage.HasValue;
            if (hasPdfPage == hasLogicalPage)
            {
                errors.Add(new PageMapValidationError("label_target",
                    $"{prefix}: set exactly one of pdf_page or logical_page."));
                continue;
            }

            if (hasPdfPage && pageLabel.Target.PdfPage!.Value < 1)
            {
                errors.Add(new PageMapValidationError("label_target",
                    $"{prefix}: pdf_page must be a 1-based PDF page."));
            }

            if (string.IsNullOrWhiteSpace(pageLabel.Label))
            {
                errors.Add(new PageMapValidationError("label_empty",
                    $"{prefix}: label must be a non-empty string."));
            }
            else if (pageLabel.Label.Length > MaxLabelLength)
            {
                errors.Add(new PageMapValidationError("label_length",
                    $"{prefix}: label is longer than {MaxLabelLength} characters."));
            }

            string targetKey = hasPdfPage
                ? $"pdf:{pageLabel.Target.PdfPage!.Value}"
                : $"box:{pageLabel.Target.LogicalPage!.Value}";
            if (!seenTargets.Add(targetKey))
            {
                errors.Add(new PageMapValidationError("label_duplicate",
                    $"{prefix}: duplicate label override for the same target; explicit labels must not " +
                    "contain conflicting duplicates on the same target."));
            }
        }
    }

    private static void ValidateChapters(DocumentPageMap map, List<PageMapValidationError> errors)
    {
        HashSet<string> seenIds = new(StringComparer.Ordinal);
        int totalNodes = 0;

        void Visit(IReadOnlyList<ChapterNode> nodes, int depth, string path)
        {
            foreach (ChapterNode node in nodes)
            {
                totalNodes++;
                if (totalNodes > MaxChapterNodes)
                {
                    errors.Add(new PageMapValidationError("chapter_count",
                        $"Too many chapter nodes; the limit is {MaxChapterNodes}."));
                    return;
                }

                string nodePath = $"{path}/{node.ChapterId}";
                if (depth > MaxChapterDepth)
                {
                    errors.Add(new PageMapValidationError("chapter_depth",
                        $"Chapter '{nodePath}' exceeds the maximum tree depth of {MaxChapterDepth}."));
                }

                if (string.IsNullOrWhiteSpace(node.ChapterId))
                {
                    errors.Add(new PageMapValidationError("chapter_id",
                        $"Chapter at '{path}' has an empty id."));
                }
                else
                {
                    if (node.ChapterId.Length > MaxChapterIdLength)
                    {
                        errors.Add(new PageMapValidationError("chapter_id",
                            $"Chapter id '{node.ChapterId}' is longer than {MaxChapterIdLength} characters."));
                    }

                    if (!seenIds.Add(node.ChapterId))
                    {
                        errors.Add(new PageMapValidationError("chapter_id_duplicate",
                            $"Duplicate chapter id '{node.ChapterId}'. Chapter ids must be unique across " +
                            "the whole tree."));
                    }
                }

                if (string.IsNullOrWhiteSpace(node.Title))
                {
                    errors.Add(new PageMapValidationError("chapter_title",
                        $"Chapter '{nodePath}' has an empty title."));
                }
                else if (node.Title.Length > MaxTitleLength)
                {
                    errors.Add(new PageMapValidationError("chapter_title",
                        $"Chapter '{nodePath}' has a title longer than {MaxTitleLength} characters."));
                }

                if (node.Target.PdfPage < 1)
                {
                    errors.Add(new PageMapValidationError("chapter_target",
                        $"Chapter '{nodePath}' targets PDF page {node.Target.PdfPage}; " +
                        "targets use 1-based PDF pages."));
                }

                if (node.Children.Count > MaxChildrenPerNode)
                {
                    errors.Add(new PageMapValidationError("chapter_children",
                        $"Chapter '{nodePath}' has too many children ({node.Children.Count}); " +
                        $"the limit is {MaxChildrenPerNode}."));
                }

                Visit(node.Children, depth + 1, nodePath);
            }
        }

        Visit(map.Chapters, 1, "chapters");
    }
}
