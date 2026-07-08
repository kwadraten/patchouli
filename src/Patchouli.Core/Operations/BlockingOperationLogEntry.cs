using Patchouli.Core.Ids;

namespace Patchouli.Core.Operations;

public sealed record BlockingOperationLogEntry(
    BlockingOperationLogEntryId EntryId,
    BlockingOperationId OperationId,
    string Level,
    string Message,
    string? Detail,
    string? ScopeType,
    string? ScopeId,
    DateTimeOffset CreatedAt);
