using Patchouli.Core.Ids;

namespace Patchouli.Core.Pagination;

/// <summary>
/// The current observable state of one logical page, read from the document's current
/// committed revisions. Only identity, physical page placement, and sibling order are
/// carried: text-only changes never appear here, so they can never invalidate a rule.
/// </summary>
public sealed record CurrentLogicalPageState(
    DocumentBoxId BoxId,
    int PdfPage,
    DocumentBoxId? NextSiblingBoxId);

/// <summary>
/// Pure logical-page invalidation. Each logical pagination rule carries its structural
/// signature (identity + order, i.e. the reading_order written at write time) inside the
/// stored definition. This check compares that signature against the current committed
/// state:
/// <list type="bullet">
/// <item>text-only changes do not invalidate, because identity, page placement, and sibling
/// order are unchanged;</item>
/// <item>deletion, addition, page moves (reorder), and disappearing endpoints mark the
/// affected rule as needing repair;</item>
/// <item>a rule needing repair pauses computation but keeps its original definition, so one
/// bad reference never makes the whole map unreadable.</item>
/// </list>
/// </summary>
public static class PageMapInvalidation
{
    public static IReadOnlyList<LogicalRuleHealth> CheckHealth(
        DocumentPageMap map,
        IReadOnlyList<CurrentLogicalPageState> currentLogicalPages)
    {
        Dictionary<DocumentBoxId, CurrentLogicalPageState> currentById =
            currentLogicalPages.ToDictionary(state => state.BoxId);

        List<LogicalRuleHealth> health = new(map.LogicalRules.Count);
        for (int i = 0; i < map.LogicalRules.Count; i++)
        {
            health.Add(CheckRule(i, map.LogicalRules[i], currentById));
        }

        return health;
    }

    private static LogicalRuleHealth CheckRule(
        int ruleIndex,
        LogicalPaginationRule rule,
        IReadOnlyDictionary<DocumentBoxId, CurrentLogicalPageState> currentById)
    {
        List<MapDiagnostic> diagnostics = [];
        IReadOnlyList<LogicalPageReadingEntry> stored = rule.ReadingOrder;

        HashSet<DocumentBoxId> storedIds = stored.Select(entry => entry.BoxId).ToHashSet();
        HashSet<DocumentBoxId> currentIdsInRange = currentById.Values
            .Where(state => state.PdfPage >= rule.StartPdfPage && state.PdfPage <= rule.EndPdfPage)
            .Select(state => state.BoxId)
            .ToHashSet();

        // Deletion / disappearing endpoints.
        for (int i = 0; i < stored.Count; i++)
        {
            LogicalPageReadingEntry entry = stored[i];
            if (!currentById.TryGetValue(entry.BoxId, out CurrentLogicalPageState? current))
            {
                bool isEndpoint = i == 0 || i == stored.Count - 1;
                diagnostics.Add(new MapDiagnostic(
                    isEndpoint ? "logical_endpoint_missing" : "logical_page_deleted",
                    isEndpoint
                        ? $"Logical page {entry.BoxId} (a range endpoint of the rule) no longer exists; " +
                          "the rule needs repair."
                        : $"Logical page {entry.BoxId} no longer exists; the rule needs repair."));
                continue;
            }

            // Page moves change the physical-then-reading enumeration, i.e. a reorder.
            if (current.PdfPage != entry.PdfPage)
            {
                diagnostics.Add(new MapDiagnostic("logical_page_moved",
                    $"Logical page {entry.BoxId} moved from PDF page {entry.PdfPage} to " +
                    $"PDF page {current.PdfPage}; the rule needs repair."));
            }
        }

        // Addition.
        foreach (DocumentBoxId added in currentIdsInRange)
        {
            if (!storedIds.Contains(added))
            {
                CurrentLogicalPageState state = currentById[added];
                diagnostics.Add(new MapDiagnostic("logical_page_added",
                    $"A new logical page {added} appeared on PDF page {state.PdfPage} inside the rule " +
                    "range; the rule needs repair."));
            }
        }

        // Same-page reorder, detected only on positive sibling-chain evidence: for any pair
        // ordered by the current sibling chain, the stored reading order must agree.
        Dictionary<DocumentBoxId, int> storedIndex = new();
        for (int i = 0; i < stored.Count; i++)
        {
            storedIndex[stored[i].BoxId] = i;
        }

        foreach (IGrouping<int, CurrentLogicalPageState> pageGroup in currentById.Values
                     .Where(state => storedIds.Contains(state.BoxId))
                     .GroupBy(state => state.PdfPage))
        {
            CheckSiblingOrder(pageGroup.ToArray(), storedIndex, diagnostics);
            if (diagnostics.Any(diagnostic => diagnostic.Code == "logical_page_reordered"))
            {
                break;
            }
        }

        if (diagnostics.Count == 0)
        {
            return LogicalRuleHealth.Healthy(ruleIndex);
        }

        // Pause computation for the affected rule but keep the original definition.
        List<MapDiagnostic> paused = new(diagnostics)
        {
            new("rule_paused",
                "Computation for this logical_page rule is paused until it is repaired; the original " +
                "definition is kept.")
        };
        return new LogicalRuleHealth(ruleIndex, true, paused);
    }

    private static void CheckSiblingOrder(
        CurrentLogicalPageState[] pageBoxes,
        IReadOnlyDictionary<DocumentBoxId, int> storedIndex,
        List<MapDiagnostic> diagnostics)
    {
        // Walk every sibling chain on the page; a chain is positive evidence of current order.
        Dictionary<DocumentBoxId, CurrentLogicalPageState> byId =
            pageBoxes.ToDictionary(state => state.BoxId);
        HashSet<DocumentBoxId> hasPredecessor = new();
        foreach (CurrentLogicalPageState state in pageBoxes)
        {
            if (state.NextSiblingBoxId.HasValue && byId.ContainsKey(state.NextSiblingBoxId.Value))
            {
                hasPredecessor.Add(state.NextSiblingBoxId.Value);
            }
        }

        foreach (CurrentLogicalPageState head in pageBoxes)
        {
            if (hasPredecessor.Contains(head.BoxId))
            {
                continue;
            }

            // (box, stored reading-order index) along the chain.
            List<(DocumentBoxId BoxId, int StoredIndex)> chain = [];
            CurrentLogicalPageState current = head;
            HashSet<DocumentBoxId> visited = [];
            while (visited.Add(current.BoxId))
            {
                if (storedIndex.TryGetValue(current.BoxId, out int index))
                {
                    chain.Add((current.BoxId, index));
                }

                if (!current.NextSiblingBoxId.HasValue ||
                    !byId.TryGetValue(current.NextSiblingBoxId.Value, out CurrentLogicalPageState? next))
                {
                    break;
                }

                current = next;
            }

            for (int i = 1; i < chain.Count; i++)
            {
                if (chain[i].StoredIndex < chain[i - 1].StoredIndex)
                {
                    diagnostics.Add(new MapDiagnostic("logical_page_reordered",
                        $"Logical pages {chain[i - 1].BoxId} and {chain[i].BoxId} changed their relative " +
                        "order; the rule needs repair."));
                    return;
                }
            }
        }
    }
}
