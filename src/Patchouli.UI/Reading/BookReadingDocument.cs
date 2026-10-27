using Patchouli.Core.Documents;
using Patchouli.Reading;

namespace Patchouli.UI.Reading;

/// <summary>Assembles the concatenated whole-book reading scenes from the per-page streamed
/// caches. In compare mode the two columns pair blocks by index, so every page must span the same
/// number of rows in each: a page with no translation fills its rows with muted 未翻译
/// placeholders, and a block-count mismatch pads the shorter side — otherwise the rows of later
/// pages shift up and the columns drift out of alignment. Outside compare mode the source column
/// passes through untouched and no translation scene is produced.</summary>
internal static class BookReadingDocument
{
    public static (ReadingScene Source, ReadingScene? Translation) Assemble(
        IEnumerable<KeyValuePair<int, ReadingScene>> sourcePages,
        IReadOnlyDictionary<int, ReadingScene?> translationPages,
        bool comparing,
        bool translationOnly = false)
    {
        List<ReadingBlock> sourceBlocks = [];
        List<ReadingBlock> translationBlocks = [];
        bool anyTranslation = false;
        foreach ((int pageIndex, ReadingScene scene) in sourcePages)
        {
            ReadingScene? translation = translationPages.GetValueOrDefault(pageIndex);
            if (translation is not null)
            {
                anyTranslation = true;
            }

            if (translationOnly)
            {
                IEnumerable<ReadingBlock> blocks = translation?.Blocks ?? scene.Blocks
                    .Select(block => ReadingBlock.Untranslated(block.BoxId, pageIndex)).ToArray();
                sourceBlocks.AddRange(blocks.Select(block => block with { PageIndex = pageIndex }));
                continue;
            }

            int pageRows = comparing
                ? Math.Max(scene.Blocks.Count, translation?.Blocks.Count ?? 0)
                : scene.Blocks.Count;
            for (int row = 0; row < pageRows; row++)
            {
                ReadingBlock? sourceBlock = row < scene.Blocks.Count ? scene.Blocks[row] : null;
                sourceBlocks.Add(sourceBlock is { } source
                    ? source with { PageIndex = pageIndex }
                    : ReadingBlock.Empty(pageIndex));
                if (comparing)
                {
                    translationBlocks.Add(translation is not null && row < translation.Blocks.Count
                        ? translation.Blocks[row] with { PageIndex = pageIndex }
                        : ReadingBlock.Untranslated(sourceBlock?.BoxId, pageIndex));
                }
            }
        }

        if (!comparing && !translationOnly)
        {
            sourceBlocks = CombineContinuations(sourceBlocks);
        }

        return (new ReadingScene(sourceBlocks),
            comparing && anyTranslation ? new ReadingScene(translationBlocks) : null);
    }

    // Continuations are explicit box relationships, not adjacency. Keep the first fragment in its
    // reading-order position and remove only its linked descendants; unrelated blocks retain order.
    private static List<ReadingBlock> CombineContinuations(List<ReadingBlock> blocks)
    {
        Dictionary<Core.Ids.DocumentBoxId, int> ownerByBox = [];
        for (int index = 0; index < blocks.Count; index++)
        {
            foreach (Core.Ids.DocumentBoxId id in BoxIds(blocks[index]))
            {
                ownerByBox.TryAdd(id, index);
            }
        }

        Dictionary<Core.Ids.DocumentBoxId, List<int>> followers = [];
        for (int index = 0; index < blocks.Count; index++)
        {
            if (blocks[index].ContinuesFromBoxId is not { } parent ||
                ownerByBox.GetValueOrDefault(parent, -1) == index)
            {
                continue;
            }

            if (!followers.TryGetValue(parent, out List<int>? list))
            {
                list = [];
                followers.Add(parent, list);
            }

            list.Add(index);
        }

        HashSet<int> consumed = [];
        List<int> deferred = [];
        List<(int Index, ReadingBlock Block)> result = [];
        for (int index = 0; index < blocks.Count; index++)
        {
            if (consumed.Contains(index))
            {
                continue;
            }

            ReadingBlock head = blocks[index];
            if (head.ContinuesFromBoxId is { } loadedParent &&
                ownerByBox.TryGetValue(loadedParent, out int parentIndex) && parentIndex != index)
            {
                deferred.Add(index);
                continue;
            }

            if (head.Kind != "paragraph")
            {
                result.Add((index, head with
                {
                    ContinuationDiagnostic = head.ContinuesFromBoxId is null
                        ? null
                        : $"Explicit continuation retained as separate {head.Kind} fragments; only paragraph chains are combined safely."
                }));
                continue;
            }

            List<int> chain = [index];
            HashSet<int> seenBlocks = [index];
            HashSet<Core.Ids.DocumentBoxId> chainIds = [.. BoxIds(head)];
            HashSet<Core.Ids.DocumentBoxId> seenIds = [.. chainIds];
            bool cycle = false;
            while (true)
            {
                int[] linked = chainIds.Where(followers.ContainsKey)
                    .SelectMany(id => followers[id]).Distinct().ToArray();
                if (linked.Any(seenBlocks.Contains))
                {
                    cycle = true;
                    break;
                }

                int[] candidates = linked.Where(candidate => !seenBlocks.Contains(candidate)).ToArray();
                if (candidates.Length != 1)
                {
                    if (candidates.Length > 1)
                    {
                        head = head with
                        {
                            ContinuationDiagnostic = "Continuation fork retained as physical fragments."
                        };
                    }

                    break;
                }

                int next = candidates[0];
                ReadingBlock nextBlock = blocks[next];
                if (nextBlock.Kind != "paragraph" || BoxIds(nextBlock).Any(id => !seenIds.Add(id)))
                {
                    break;
                }

                chain.Add(next);
                seenBlocks.Add(next);
                chainIds = [.. BoxIds(nextBlock)];
            }

            if (cycle)
            {
                foreach (int fragmentIndex in chain)
                {
                    ReadingBlock fragment = blocks[fragmentIndex] with
                    {
                        ContinuationDiagnostic = "Continuation cycle retained as physical fragments."
                    };
                    result.Add((fragmentIndex, fragment));
                    if (fragmentIndex != index)
                    {
                        consumed.Add(fragmentIndex);
                    }
                }

                continue;
            }

            if (chain.Count == 1)
            {
                result.Add((index, head));
                continue;
            }

            string text = head.Text;
            List<ReadingBlockSegment> segments = head.Segments?.ToList() ?? [];
            List<Core.Ids.DocumentBoxId> ids = [.. BoxIds(head)];
            if (segments.Count == 0)
            {
                segments.AddRange(ids.Select(id => new ReadingBlockSegment(0, text.Length, head.PageIndex, id)));
            }

            bool hasInlines = chain.Any(childIndex => blocks[childIndex].Inlines is { Count: > 0 });
            List<MarkdownInlineModel> inlines = hasInlines
                ? head.Inlines?.ToList() ?? [new MarkdownInlineModel("text", head.Text)]
                : [];
            foreach (int childIndex in chain.Skip(1))
            {
                ReadingBlock child = blocks[childIndex];
                int start = text.Length + 1;
                text += "\n" + child.Text;
                List<ReadingBlockSegment> childSegments = child.Segments?.ToList() ??
                                                          BoxIds(child).Select(id =>
                                                              new ReadingBlockSegment(0, child.Text.Length,
                                                                  child.PageIndex, id)).ToList();
                segments.AddRange(childSegments.Select(segment => segment with { Start = segment.Start + start }));
                ids.AddRange(BoxIds(child).Where(id => !ids.Contains(id)));
                if (hasInlines)
                {
                    inlines.Add(new MarkdownInlineModel("text", "\n"));
                    inlines.AddRange(child.Inlines ?? [new MarkdownInlineModel("text", child.Text)]);
                }

                consumed.Add(childIndex);
            }

            result.Add((index, head with
            {
                Text = text,
                Inlines = inlines.Count == 0 ? null : inlines,
                Segments = segments,
                SourceBoxIds = ids
            }));
        }

        foreach (int index in deferred.Where(index => !consumed.Contains(index)))
        {
            result.Add((index, blocks[index]));
        }

        return result.OrderBy(item => item.Index).Select(item => item.Block).ToList();
    }

    private static IReadOnlyList<Core.Ids.DocumentBoxId> BoxIds(ReadingBlock block)
    {
        return block.SourceBoxIds is { Count: > 0 } ids
            ? ids
            : block.BoxId is { } id
                ? [id]
                : [];
    }
}
