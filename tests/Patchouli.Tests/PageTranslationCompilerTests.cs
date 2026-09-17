using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Documents.Translations;

namespace Patchouli.Tests;

public sealed class PageTranslationCompilerTests
{
    [Fact]
    public async Task Compiled_translation_keeps_the_source_block_structure()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentTreeRevision revision = await CommitStructuredPageAsync(context);
        CompiledMarkdown source = await context.CompileSourceAsync(revision.TreeRevisionId);
        await PutAsync(context, Translate(source.Markdown));
        int version = await context.GetVersionAsync();

        Result<TranslatedPageMarkdown> compiled = await context.TranslationCompiler.CompilePageTranslationAsync(
            context.PageId, revision.TreeRevisionId, version);

        compiled.IsSuccess.Should().BeTrue(compiled.ErrorMessage);
        string markdown = compiled.Value.Markdown;
        markdown.Should().Contain("# 翻译标题");
        markdown.Should().Contain("$$\nE = mc^3\n$$");
        markdown.Should().Contain("```csharp\nint y = 2;\n```");
        markdown.Should().Contain("翻译段落");
        IReadOnlyList<MarkdownBlock> sourceBlocks = context.Markdown.Parse(source.Markdown).Blocks;
        IReadOnlyList<MarkdownBlock> translatedBlocks = context.Markdown.Parse(markdown).Blocks;
        translatedBlocks.Select(block => (block.Kind, block.Level))
            .Should().Equal(sourceBlocks.Select(block => (block.Kind, block.Level)));
    }

    [Fact]
    public async Task Source_map_entries_correspond_one_to_one_with_the_source_boxes()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentTreeRevision revision = await CommitStructuredPageAsync(context);
        CompiledMarkdown source = await context.CompileSourceAsync(revision.TreeRevisionId);
        await PutAsync(context, Translate(source.Markdown));
        int version = await context.GetVersionAsync();

        TranslatedPageMarkdown compiled = (await context.TranslationCompiler.CompilePageTranslationAsync(
            context.PageId, revision.TreeRevisionId, version)).Value;

        compiled.SourceMap.Select(entry => entry.BoxId).Should().Equal(source.SourceMap.Select(entry => entry.BoxId));
        IReadOnlyList<DocumentBox> contentBoxes =
            (await context.ListBoxesAsync(revision.TreeRevisionId))
            .Where(box => box.BoxType != DocumentBoxType.LogicalPage)
            .ToArray();
        compiled.SourceMap.Should().HaveCount(contentBoxes.Count);
        foreach (MarkdownSourceMapEntry entry in compiled.SourceMap)
        {
            string slice = compiled.Markdown.Substring(entry.Start, entry.Length);
            slice.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Rewriting_the_translation_changes_the_compiled_markdown()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentTreeRevision revision = await CommitStructuredPageAsync(context);
        CompiledMarkdown source = await context.CompileSourceAsync(revision.TreeRevisionId);
        await PutAsync(context, Translate(source.Markdown));

        TranslatedPageMarkdown first = (await context.Translations.GetPageTranslationAsync(
            context.DocumentId, context.PageId))!;
        first.Markdown.Should().Contain("翻译段落");

        await PutAsync(context, Translate(source.Markdown).Replace("翻译段落", "重写后的段落"));
        TranslatedPageMarkdown second = (await context.Translations.GetPageTranslationAsync(
            context.DocumentId, context.PageId))!;

        second.Markdown.Should().Contain("重写后的段落").And.NotContain("翻译段落");
        (await context.GetVersionAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Cache_keys_on_page_revision_and_version()
    {
        PageTranslationCache cache = new();
        PageId page = PageId.New();
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        DocumentTreeRevisionId otherRevision = DocumentTreeRevisionId.New();
        int calls = 0;

        Result<TranslatedPageMarkdown> Factory(string marker)
        {
            calls++;
            return Result<TranslatedPageMarkdown>.Success(new TranslatedPageMarkdown(
                marker, [], new PageTranslationStatus(0, 0, [], revision, true)));
        }

        Result<TranslatedPageMarkdown> first = await cache.GetOrCreateAsync(
            page, revision, 1, _ => Task.FromResult(Factory("v1")));
        Result<TranslatedPageMarkdown> cached = await cache.GetOrCreateAsync(
            page, revision, 1, _ => Task.FromResult(Factory("unused")));
        Result<TranslatedPageMarkdown> bumped = await cache.GetOrCreateAsync(
            page, revision, 2, _ => Task.FromResult(Factory("v2")));
        Result<TranslatedPageMarkdown> otherTree = await cache.GetOrCreateAsync(
            page, otherRevision, 1, _ => Task.FromResult(Factory("other-rev")));

        first.Value.Markdown.Should().Be("v1");
        cached.Value.Markdown.Should().Be("v1");
        bumped.Value.Markdown.Should().Be("v2");
        otherTree.Value.Markdown.Should().Be("other-rev");
        calls.Should().Be(3, "an identical key reuses the cached compilation");

        cache.Invalidate(page);
        Result<TranslatedPageMarkdown> afterInvalidate = await cache.GetOrCreateAsync(
            page, revision, 1, _ => Task.FromResult(Factory("v1-rebuilt")));
        afterInvalidate.Value.Markdown.Should().Be("v1-rebuilt");
        calls.Should().Be(4);
    }

    private static async Task<DocumentTreeRevision> CommitStructuredPageAsync(TranslationTestContext context)
    {
        DocumentBoxId logical = DocumentBoxId.New();
        return await context.CommitAsync(
            new DocumentBoxSeed(logical, null, 0, DocumentBoxType.LogicalPage, null, null,
                new NormalizedBBox(0, 0, 1, 1), null),
            new DocumentBoxSeed(DocumentBoxId.New(), logical, 0, DocumentBoxType.Title, null, null,
                new NormalizedBBox(.1, .05, .8, .05), new TextBoxPayload("Original title"), 1),
            new DocumentBoxSeed(DocumentBoxId.New(), logical, 1, DocumentBoxType.Text, null, null,
                new NormalizedBBox(.1, .15, .8, .05), new TextBoxPayload("Original paragraph")),
            new DocumentBoxSeed(DocumentBoxId.New(), logical, 2, DocumentBoxType.Equation, null, null,
                new NormalizedBBox(.1, .25, .8, .05), new EquationBoxPayload("E = mc^2")),
            new DocumentBoxSeed(DocumentBoxId.New(), logical, 3, DocumentBoxType.Code, null, null,
                new NormalizedBBox(.1, .35, .8, .05), new CodeBoxPayload("int x = 1;"), CodeLanguage: "csharp"));
    }

    private static string Translate(string source)
    {
        return source
            .Replace("Original title", "翻译标题")
            .Replace("Original paragraph", "翻译段落")
            .Replace("E = mc^2", "E = mc^3")
            .Replace("int x = 1;", "int y = 2;");
    }

    private static async Task PutAsync(TranslationTestContext context, string markdown)
    {
        Result<PageTranslationStatus> put = await context.Translations.PutPageTranslationAsync(
            context.DocumentId, context.PageId, markdown);
        put.IsSuccess.Should().BeTrue(put.ErrorMessage);
    }
}
