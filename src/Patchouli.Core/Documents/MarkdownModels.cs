using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Documents;

public sealed record CompiledMarkdown(
    string Markdown,
    IReadOnlyList<MarkdownSourceMapEntry> SourceMap,
    IReadOnlyList<MarkdownDiagnostic> Diagnostics);

public sealed record MarkdownSourceMapEntry(
    DocumentBoxId BoxId,
    int Start,
    int Length,
    int PreviewNodeStart,
    int PreviewNodeCount);

public sealed record MarkdownDiagnostic(string Code, string Message, DocumentBoxId? BoxId = null);

public sealed record MarkdownBlock(string Kind, string Text, int Start, int Length, int Level = 0);

public sealed record MarkdownDocumentModel(IReadOnlyList<MarkdownBlock> Blocks);

public interface IMarkdownEngine
{
    MarkdownDocumentModel Parse(string markdown);

    string ToPlainText(string markdown);

    Result ValidateLeaf(string boxType, DocumentBoxPayload payload);
}

public interface IDocumentMarkdownCompiler
{
    Task<Result<CompiledMarkdown>> CompilePageMarkdownAsync(
        DocumentTreeRevisionId treeRevisionId,
        bool includeSuppressed = false,
        CancellationToken cancellationToken = default);
}
