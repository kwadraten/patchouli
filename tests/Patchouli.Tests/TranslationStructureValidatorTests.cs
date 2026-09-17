using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Infrastructure.Documents.Translations;

namespace Patchouli.Tests;

public sealed class TranslationStructureValidatorTests
{
    [Fact]
    public async Task Structurally_identical_translation_is_accepted_and_mapped_to_every_box()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        (DocumentTreeRevision revision, string source) = await CommitStandardPageAsync(context);
        IReadOnlyList<DocumentBox> boxes = await context.ListBoxesAsync(revision.TreeRevisionId);
        CompiledMarkdown compiled = await context.CompileSourceAsync(revision.TreeRevisionId);
        TranslationStructureValidator validator = new(context.Markdown);

        TranslationValidationResult result = validator.Validate(compiled, Translate(source), boxes);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        IReadOnlyList<DocumentBox> contentBoxes = boxes
            .Where(box => box.BoxType != DocumentBoxType.LogicalPage)
            .ToArray();
        result.BoxFragments.Keys.Should().BeEquivalentTo(contentBoxes.Select(box => box.BoxId));
        result.BoxFragments[contentBoxes.Single(box => box.BoxType == DocumentBoxType.Title).BoxId]
            .Should().Be("翻译标题");
        result.BoxFragments[contentBoxes.Single(box => box.BoxType == DocumentBoxType.Equation).BoxId]
            .Should().Be("E = mc^3");
        result.BoxFragments[contentBoxes.Single(box => box.BoxType == DocumentBoxType.Code).BoxId]
            .Should().Be("int y = 2;");
    }

    [Fact]
    public async Task Heading_level_change_is_reported_as_a_structural_mismatch()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        (DocumentTreeRevision revision, string source) = await CommitStandardPageAsync(context);
        CompiledMarkdown compiled = await context.CompileSourceAsync(revision.TreeRevisionId);

        TranslationValidationResult result = new TranslationStructureValidator(context.Markdown).Validate(
            compiled, Translate(source).Replace("# 翻译标题", "## 翻译标题"), await context.ListBoxesAsync(
                revision.TreeRevisionId));

        result.IsValid.Should().BeFalse();
        result.BoxFragments.Should().BeEmpty();
        result.Errors.Should().ContainSingle(error =>
            error.Expected == "heading level 1" && error.Actual == "heading level 2");
    }

    [Fact]
    public async Task Block_type_change_is_reported_at_the_matching_index()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        (DocumentTreeRevision revision, string source) = await CommitStandardPageAsync(context);
        CompiledMarkdown compiled = await context.CompileSourceAsync(revision.TreeRevisionId);

        TranslationValidationResult result = new TranslationStructureValidator(context.Markdown).Validate(
            compiled, Translate(source).Replace("翻译段落", "- 列表项"), await context.ListBoxesAsync(
                revision.TreeRevisionId));

        result.IsValid.Should().BeFalse();
        TranslationStructureError error = result.Errors.Should()
            .ContainSingle(candidate => candidate.Expected == "paragraph" && candidate.Actual == "list").Subject;
        error.BlockIndex.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Extra_block_is_reported_against_end_of_markdown()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        (DocumentTreeRevision revision, string source) = await CommitStandardPageAsync(context);
        CompiledMarkdown compiled = await context.CompileSourceAsync(revision.TreeRevisionId);

        TranslationValidationResult result = new TranslationStructureValidator(context.Markdown).Validate(
            compiled, Translate(source) + "\n\n额外的段落。", await context.ListBoxesAsync(revision.TreeRevisionId));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Expected == "end of markdown" && error.Actual == "paragraph");
    }

    [Fact]
    public async Task Missing_block_is_reported_against_end_of_markdown()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        (DocumentTreeRevision revision, string source) = await CommitStandardPageAsync(context);
        CompiledMarkdown compiled = await context.CompileSourceAsync(revision.TreeRevisionId);

        string trimmed = Translate(source).Replace("\n\n第二段", string.Empty);
        TranslationValidationResult result = new TranslationStructureValidator(context.Markdown).Validate(
            compiled, trimmed, await context.ListBoxesAsync(revision.TreeRevisionId));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Expected == "paragraph" && error.Actual == "end of markdown");
    }

    [Fact]
    public async Task Table_dimension_change_is_reported_as_a_shape_mismatch()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        (DocumentTreeRevision revision, string source) = await CommitStandardPageAsync(context);
        CompiledMarkdown compiled = await context.CompileSourceAsync(revision.TreeRevisionId);
        string widened = Translate(source)
            .Replace("| 甲 | 乙 |", "| 甲 | 乙 | 丙 |")
            .Replace("| --- | --- |", "| --- | --- | --- |")
            .Replace("| 一 | 二 |", "| 一 | 二 | 三 |");

        TranslationValidationResult result = new TranslationStructureValidator(context.Markdown).Validate(
            compiled, widened, await context.ListBoxesAsync(revision.TreeRevisionId));

        result.IsValid.Should().BeFalse();
        // The descriptor counts physical markdown rows (header + separator + data rows), so a
        // header-only widening is reported as a 3x2 -> 3x3 shape change.
        result.Errors.Should().ContainSingle(error => error.Expected == "table 3x2" && error.Actual == "table 3x3");
    }

    [Fact]
    public async Task Missing_logical_page_separator_is_reported()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        (DocumentTreeRevision revision, string source) = await CommitStandardPageAsync(context);
        CompiledMarkdown compiled = await context.CompileSourceAsync(revision.TreeRevisionId);
        string withoutSeparator = Translate(source)
            .Replace("---\n\n", string.Empty)
            .Replace("---\r\n\r\n", string.Empty);

        TranslationValidationResult result = new TranslationStructureValidator(context.Markdown).Validate(
            compiled, withoutSeparator, await context.ListBoxesAsync(revision.TreeRevisionId));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Expected == "thematic_break");
    }

    [Fact]
    public async Task Missing_code_fence_is_reported()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        (DocumentTreeRevision revision, string source) = await CommitStandardPageAsync(context);
        CompiledMarkdown compiled = await context.CompileSourceAsync(revision.TreeRevisionId);
        string withoutFence = Translate(source)
            .Replace("```csharp\nint y = 2;\n```", "int y = 2;");

        TranslationValidationResult result = new TranslationStructureValidator(context.Markdown).Validate(
            compiled, withoutFence, await context.ListBoxesAsync(revision.TreeRevisionId));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Expected == "code" && error.Actual == "paragraph");
    }

    private static async Task<(DocumentTreeRevision Revision, string Source)> CommitStandardPageAsync(
        TranslationTestContext context)
    {
        DocumentBoxId logicalFirst = DocumentBoxId.New();
        DocumentBoxId logicalSecond = DocumentBoxId.New();
        DocumentTreeRevision revision = await context.CommitAsync(
            new DocumentBoxSeed(logicalFirst, null, 0, DocumentBoxType.LogicalPage, null, null,
                new NormalizedBBox(0, 0, 1, .5), null),
            new DocumentBoxSeed(DocumentBoxId.New(), logicalFirst, 0, DocumentBoxType.Title, null, null,
                new NormalizedBBox(.1, .05, .8, .05), new TextBoxPayload("Original title"), 1),
            new DocumentBoxSeed(DocumentBoxId.New(), logicalFirst, 1, DocumentBoxType.Text, null, null,
                new NormalizedBBox(.1, .15, .8, .05), new TextBoxPayload("Original paragraph")),
            new DocumentBoxSeed(DocumentBoxId.New(), logicalFirst, 2, DocumentBoxType.Equation, null, null,
                new NormalizedBBox(.1, .25, .8, .05), new EquationBoxPayload("E = mc^2")),
            new DocumentBoxSeed(DocumentBoxId.New(), logicalFirst, 3, DocumentBoxType.Code, null, null,
                new NormalizedBBox(.1, .35, .8, .05), new CodeBoxPayload("int x = 1;"), CodeLanguage: "csharp"),
            new DocumentBoxSeed(logicalSecond, null, 1, DocumentBoxType.LogicalPage, null, null,
                new NormalizedBBox(0, .5, 1, .5), null),
            new DocumentBoxSeed(DocumentBoxId.New(), logicalSecond, 0, DocumentBoxType.Table, null, null,
                new NormalizedBBox(.1, .55, .8, .1),
                new TableBoxPayload("| a | b |\n| --- | --- |\n| 1 | 2 |")),
            new DocumentBoxSeed(DocumentBoxId.New(), logicalSecond, 1, DocumentBoxType.Text, null, null,
                new NormalizedBBox(.1, .75, .8, .05), new TextBoxPayload("Second paragraph")));
        CompiledMarkdown compiled = await context.CompileSourceAsync(revision.TreeRevisionId);
        return (revision, compiled.Markdown);
    }

    private static string Translate(string source)
    {
        return source
            .Replace("Original title", "翻译标题")
            .Replace("Original paragraph", "翻译段落")
            .Replace("E = mc^2", "E = mc^3")
            .Replace("int x = 1;", "int y = 2;")
            .Replace("| a | b |", "| 甲 | 乙 |")
            .Replace("| 1 | 2 |", "| 一 | 二 |")
            .Replace("Second paragraph", "第二段");
    }
}
