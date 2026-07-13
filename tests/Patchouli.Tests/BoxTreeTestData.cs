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
        Result<DocumentTreeRevision> staging = await service.StagePageAsync(
            documentInstanceId,
            pageId,
            [
                new DocumentBoxSeed(null, null, 0, boxType, null, null,
                    new NormalizedBBox(.1, .1, .8, .1), Payload(boxType, text),
                    boxType == DocumentBoxType.Title ? 1 : null, Suppressed: suppressed)
            ],
            DocumentTreeRevisionSource.Import);
        if (staging.IsFailure)
        {
            throw new InvalidOperationException(staging.ErrorMessage);
        }

        Result<DocumentTreeRevision> committed = await service.AdoptStagingRevisionAsync(staging.Value.TreeRevisionId);
        if (committed.IsFailure)
        {
            throw new InvalidOperationException(committed.ErrorMessage);
        }

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
