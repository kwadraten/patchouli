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
        bool comparing)
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

        return (new ReadingScene(sourceBlocks),
            comparing && anyTranslation ? new ReadingScene(translationBlocks) : null);
    }
}
