using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Bibliography.Bibtex;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography.Biblatex;

namespace Patchouli.Tests.CitationInterop;

/// <summary>
/// Plan §7 validation: classic BibTeX produced by
/// <see cref="BibtexExportMapper"/> + <see cref="BibtexSerializer"/> is
/// re-parsed by the independent Rust helper parser — not merely checked
/// against the mapper's own expectations. (The helper's WRITE path is
/// deliberately not used for classic output: it canonicalizes journal /
/// address / school to journaltitle / location / institution.) Skips when
/// the helper binary is not staged next to the test assembly (same
/// convention as BiblatexImportCoreTests).
/// </summary>
public sealed class BibtexClassicHelperValidationTests
{
    [Fact]
    public async Task Classic_output_roundtrips_through_the_real_helper()
    {
        string helper = BiblatexHelperClient.ResolveDefaultHelperPath();
        if (!File.Exists(helper))
        {
            return;
        }

        BiblatexHelperClient client = new(helper);
        ItemId id = ItemId.New();
        ItemMetadata article = new(
            id,
            LibraryId.New(),
            "article-journal",
            "doe2020testing",
            "On Testing",
            null,
            null,
            "[]",
            [
                new ItemCreator("c1", id, ItemCreatorRoles.Author, "Doe", "John", null, null, null, 0,
                    DateTimeOffset.UtcNow)
            ],
            null,
            [
                new ItemDate("d1", id, ItemDateRoles.Issued, "[[2020,5]]", false, null, null,
                    DateTimeOffset.UtcNow)
            ],
            [],
            "Journal of Testing",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "12",
            null,
            "3",
            "33-41",
            null,
            null,
            null,
            null,
            "[]",
            "[]",
            "{}",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        Result<BibtexExportResult> mapped = BibtexExportMapper.MapItems([article]);
        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);

        string written = BibtexSerializer.Serialize(mapped.Value.Entries);
        written.Should().Contain("@article{doe2020testing");
        written.Should().Contain("journal = {Journal of Testing}");
        written.Should().NotContain("journaltitle");

        Result<IReadOnlyList<BiblatexEntryDto>> reparsed = await client.ParseAsync(written);
        reparsed.IsSuccess.Should().BeTrue(reparsed.ErrorMessage);
        BiblatexEntryDto entry = reparsed.Value.Should().ContainSingle().Subject;
        entry.EntryType.Should().Be("article");
        entry.Key.Should().Be("doe2020testing");
        entry.Fields["journal"].Should().Be("Journal of Testing");
        entry.Fields["year"].Should().Be("2020");
        // The independent parser normalizes the classic "--" range dash to an
        // en-dash on read; the written form keeps the classic "--".
        entry.Fields["pages"].Should().Be("33–41");
        entry.Fields.Should().NotContainKey("journaltitle");
        entry.Fields.Should().NotContainKey("date");
    }
}
