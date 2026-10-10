using System.Text.Json;

namespace Patchouli.Host.Agent;

public sealed partial class AgentSessionStore
{
    private const string ToolPayloadLogKind = "context/tool-payloads";
    private const string CompactionLogKind = "context/compaction";
    private readonly SemaphoreSlim _contextJournalGate = new(1, 1);
    private readonly Dictionary<string, ContextJournal> _contextJournals = new(StringComparer.Ordinal);

    private sealed record ContextJournal(AgentToolPayloadCheckpoint? ToolPayloads, AgentContextCheckpoint? Compaction);

    public async Task<AgentToolPayloadCheckpoint?> ReadToolPayloadsAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        await _contextJournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadContextJournalAsync(sessionId, cancellationToken).ConfigureAwait(false)).ToolPayloads;
        }
        finally
        {
            _contextJournalGate.Release();
        }
    }

    public async Task WriteToolPayloadsAsync(string sessionId, AgentToolPayloadCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await _contextJournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ContextJournal journal = await LoadContextJournalAsync(sessionId, cancellationToken).ConfigureAwait(false);
            int start = journal.ToolPayloads?.Entries ?? 0;
            if (checkpoint.Entries <= start)
            {
                throw new InvalidDataException("Tool payload decisions must append to the existing history prefix.");
            }

            AgentToolPayloadDelta delta = new(start, checkpoint.Entries, checkpoint.PrefixHash,
                checkpoint.Payloads.Where(pair => pair.Key >= start)
                    .ToDictionary(pair => pair.Key, pair => pair.Value));
            cancellationToken.ThrowIfCancellationRequested();
            // Once admitted, settle the log record even if the model request is cancelled. The
            // journal is authoritative; the snapshot is only a convenience for inspection.
            await AppendLogAsync(sessionId, ToolPayloadLogKind, JsonSerializer.Serialize(delta), CancellationToken.None)
                .ConfigureAwait(false);
            _contextJournals[sessionId] = journal with { ToolPayloads = checkpoint };
            await WriteAtomicAsync(Path.Combine(ResolveSessionDirectory(sessionId), "tool-payloads.json"),
                JsonSerializer.Serialize(checkpoint), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _contextJournalGate.Release();
        }
    }

    /// <summary>Reads the model-prefix checkpoint; original history remains in context.json and the journal.</summary>
    public async Task<AgentContextCheckpoint?> ReadCompactionAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        await _contextJournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadContextJournalAsync(sessionId, cancellationToken).ConfigureAwait(false)).Compaction;
        }
        finally
        {
            _contextJournalGate.Release();
        }
    }

    public async Task WriteCompactionAsync(string sessionId, AgentContextCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await _contextJournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ContextJournal journal = await LoadContextJournalAsync(sessionId, cancellationToken).ConfigureAwait(false);
            string json = JsonSerializer.Serialize(checkpoint);
            cancellationToken.ThrowIfCancellationRequested();
            await AppendLogAsync(sessionId, CompactionLogKind, json, CancellationToken.None).ConfigureAwait(false);
            _contextJournals[sessionId] = journal with { Compaction = checkpoint };
            await WriteAtomicAsync(Path.Combine(ResolveSessionDirectory(sessionId), "compaction.json"), json,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _contextJournalGate.Release();
        }
    }

    private async Task<ContextJournal> LoadContextJournalAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_contextJournals.TryGetValue(sessionId, out ContextJournal? cached))
        {
            return cached;
        }

        ContextJournal journal = new(null, null);
        foreach (AgentSessionLogEntry entry in await ReadLogAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Kind == ToolPayloadLogKind)
            {
                AgentToolPayloadDelta delta = JsonSerializer.Deserialize<AgentToolPayloadDelta>(entry.Payload) ??
                                              throw new InvalidDataException("Invalid tool payload projection record.");
                if (delta.Version != 1 || delta.StartEntry != (journal.ToolPayloads?.Entries ?? 0) ||
                    delta.Entries <= delta.StartEntry ||
                    delta.Payloads is null || delta.Payloads.Any(pair => pair.Key < delta.StartEntry ||
                                                                         pair.Key >= delta.Entries ||
                                                                         pair.Value is null))
                {
                    throw new InvalidDataException(
                        "Tool payload projection records are not in original history order.");
                }

                Dictionary<int, string> payloads = journal.ToolPayloads is { } previous
                    ? new Dictionary<int, string>(previous.Payloads)
                    : [];
                foreach ((int index, string payload) in delta.Payloads)
                {
                    payloads.Add(index, payload);
                }

                journal = journal with
                {
                    ToolPayloads = new AgentToolPayloadCheckpoint(delta.Entries, delta.PrefixHash, payloads)
                };
            }
            else if (entry.Kind == CompactionLogKind)
            {
                journal = journal with
                {
                    Compaction = JsonSerializer.Deserialize<AgentContextCheckpoint>(entry.Payload) ??
                                 throw new InvalidDataException("Invalid context compaction record.")
                };
            }
        }

        _contextJournals[sessionId] = journal;
        return journal;
    }
}
