using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrLayoutDocumentTests
{
    [Fact]
    public void Validate_accepts_table_cell_metadata_on_table_cell_blocks()
    {
        var document = new OcrLayoutDocument([
            new OcrLayoutPage(
                PageId.New(),
                0,
                600,
                800,
                [
                    new OcrLayoutBlock(
                        LayoutNodeType.Table,
                        TextPolicy.AggregateChildren,
                        1,
                        Children:
                        [
                            new OcrLayoutBlock(
                                LayoutNodeType.TableRow,
                                TextPolicy.AggregateChildren,
                                2,
                                Children:
                                [
                                    new OcrLayoutBlock(
                                        LayoutNodeType.TableCell,
                                        TextPolicy.Own,
                                        3,
                                        Text: "Header",
                                        TableCell: new OcrTableCell(0, 0, 1, 1, true))
                                ])
                        ])
                ])
        ]);

        document.Validate().IsSuccess.Should().BeTrue();
        document.TotalBlockCount.Should().Be(3);
    }

    [Fact]
    public void Validate_rejects_non_positive_table_spans()
    {
        var document = new OcrLayoutDocument([
            new OcrLayoutPage(
                PageId.New(),
                0,
                null,
                null,
                [
                    new OcrLayoutBlock(
                        LayoutNodeType.TableCell,
                        TextPolicy.Own,
                        1,
                        Text: "Broken",
                        TableCell: new OcrTableCell(0, 0, 0, 1, false))
                ])
        ]);

        var validation = document.Validate();

        validation.IsFailure.Should().BeTrue();
        validation.ErrorCode.Should().Be(Patchouli.Core.Results.AppErrorCodes.ValidationFailed);
    }
}
