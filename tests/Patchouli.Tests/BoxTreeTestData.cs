using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;

namespace Patchouli.Tests;

internal static class BoxTreeTestData
{
    public static DocumentTreeService CreateService(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        return new DocumentTreeService(connectionFactory, clock, new MarkdigMarkdownEngine());
    }

    public static async Task<DocumentTreeRevision> CommitTextAsync(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        string text,
        string boxType = DocumentBoxType.Text,
        bool suppressed = false)
    {
        DocumentTreeService service = CreateService(connectionFactory, clock);
        Result<DocumentTreeRevision> working = await service.BeginWorkingRevisionAsync(
            documentInstanceId,
            pageId,
            [
                new DocumentBoxSeed(null, null, 0, boxType, null, null,
                    new NormalizedBBox(.1, .1, .8, .1), Payload(boxType, text),
                    boxType == DocumentBoxType.Title ? 1 : null, Suppressed: suppressed)
            ],
            DocumentTreeRevisionSource.Import);
        if (working.IsFailure)
        {
            throw new InvalidOperationException(working.ErrorMessage);
        }

        IReadOnlyList<DocumentBox> boxesBefore = (await service.ListBoxesAsync(working.Value.TreeRevisionId)).Value;

        Result<DocumentTreeRevision> committed = await service.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
        if (committed.IsFailure)
        {
            throw new InvalidOperationException(committed.ErrorMessage);
        }

        // In-place commit keeps the same revision id and does not copy boxes.
        committed.Value.TreeRevisionId.Should().Be(working.Value.TreeRevisionId);
        IReadOnlyList<DocumentBox> boxesAfter = (await service.ListBoxesAsync(committed.Value.TreeRevisionId)).Value;
        boxesAfter.Should().HaveCount(boxesBefore.Count);

        return committed.Value;
    }

    private static DocumentBoxPayload Payload(string boxType, string text)
    {
        return boxType switch
        {
            DocumentBoxType.Table => new TableBoxPayload(text),
            DocumentBoxType.List => new ListBoxPayload(text),
            DocumentBoxType.Code => new CodeBoxPayload(text),
            DocumentBoxType.Equation => new EquationBoxPayload(text),
            _ => new TextBoxPayload(text)
        };
    }
}
