using FluentAssertions;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography.Biblatex;

namespace Patchouli.Tests;

public sealed class BiblatexVerificationWarningTests
{
    [Theory]
    [InlineData("unpublished", "author={Doe, Jane}", "BIBLATEX_MISSING_FIELDS")]
    [InlineData("misc", "", "BIBLATEX_MISSING_FIELDS")]
    [InlineData("book", "author={Doe, Jane},date={2024}", "BIBLATEX_MISSING_FIELDS")]
    [InlineData("incollection", "author={Doe, Jane},date={2024},publisher={P},booktitle={B}",
        "BIBLATEX_MISSING_FIELDS")]
    [InlineData("inproceedings", "author={Doe, Jane},date={2024},booktitle={B},pagetotal={10}",
        "BIBLATEX_SUPERFLUOUS_FIELDS")]
    [InlineData("article", "author={Doe, Jane},date={2024},journal={J},pages={S1--S9},volume={S1}",
        "BIBLATEX_LITERAL_FIELDS")]
    public async Task Real_helper_diagnostics_allow_import_with_warnings(string type, string fields, string warning)
    {
        BiblatexEntryDto entry = await Parse($"@{type}{{k,title={{T}},{fields}}}");
        entry.VerifyOk.Should().BeFalse();
        Result<IReadOnlyList<BiblatexMappedItem>> result = BiblatexImportPlanner.MapVisibleEntries([entry]);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Single().Warnings.Should().Contain(message => message.StartsWith(warning));
        if (warning == "BIBLATEX_LITERAL_FIELDS")
        {
            result.Value.Single().Pages.Should().Be("S1–S9");
            result.Value.Single().Volume.Should().Be("S1");
        }
    }

    [Fact]
    public async Task Missing_title_still_blocks_import()
    {
        Result<BiblatexMappedItem> result =
            BiblatexFieldMapper.MapVisibleEntry(await Parse("@unpublished{k,author={Doe, Jane}}"));
        result.ErrorCode.Should().Be(AppErrorCodes.BiblatexMissingTitle);
    }

    [Fact]
    public async Task Blocking_diagnostic_is_not_hidden_by_missing_fields()
    {
        Result<BiblatexMappedItem> result =
            BiblatexFieldMapper.MapVisibleEntry(await Parse("@report{k,title={T},volumes={invalid}}"));
        result.ErrorCode.Should().Be(AppErrorCodes.BiblatexVerifyFailed);
        result.ErrorMessage.Should().Contain("volumes");
    }

    [Fact]
    public async Task Unpublished_without_note_can_be_written_and_imported_again()
    {
        BiblatexHelperClient helper = new();
        Result<string> written = await helper.WriteAsync([
            new BiblatexWriteEntryDto("k", "unpublished",
                new Dictionary<string, string> { ["title"] = "T" },
                new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>(), [])
        ]);
        written.IsSuccess.Should().BeTrue(written.ErrorMessage);
        Result<BiblatexMappedItem> result = BiblatexFieldMapper.MapVisibleEntry(await Parse(written.Value));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Note.Should().BeNull();
        result.Value.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task General_agent_projection_keeps_general_and_reports_warnings()
    {
        Result<BiblatexMappedItem> result = BiblatexFieldMapper.MapGeneralAgentEntry(await Parse("@misc{k,title={T}}"));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.ItemType.Should().Be("general");
        result.Value.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Syntax_errors_remain_fatal()
    {
        Result<IReadOnlyList<BiblatexEntryDto>> result = await new BiblatexHelperClient().ParseAsync("@book{broken,");
        result.ErrorCode.Should().Be(AppErrorCodes.BiblatexParseFailed);
    }

    private static async Task<BiblatexEntryDto> Parse(string text)
    {
        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await new BiblatexHelperClient().ParseAsync(text);
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        return parsed.Value.Single();
    }
}
