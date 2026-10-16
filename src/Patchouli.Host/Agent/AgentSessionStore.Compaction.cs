using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Patchouli.Host.Agent;

public sealed partial class AgentSessionStore
{
    private const string ToolPayloadLogKind = "context/tool-payloads";
    private const string CompactionLogKind = "context/compaction";
    private readonly SemaphoreSlim _contextJournalGate = new(1, 1);

    private readonly Dictionary<string, Dictionary<ContextKey, ContextJournal>> _contextJournals =
        new(StringComparer.Ordinal);

    private readonly record struct ContextKey(string ContextId, string ActivationId);

    private sealed record ContextJournal(
        AgentToolPayloadCheckpoint? ToolPayloads,
        AgentContextCheckpoint? Compaction,
        long Sequence = 0);

    public async Task<AgentToolPayloadCheckpoint?> ReadToolPayloadsAsync(string sessionId,
        CancellationToken cancellationToken, string? contextId = null, string? activationId = null)
    {
        await _contextJournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<ContextKey, ContextJournal> journals =
                await LoadContextJournalsAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return journals.GetValueOrDefault(Key(sessionId, contextId, activationId))?.ToolPayloads;
        }
        finally
        {
            _contextJournalGate.Release();
        }
    }

    public async Task WriteToolPayloadsAsync(string sessionId, AgentToolPayloadCheckpoint checkpoint,
        CancellationToken cancellationToken, string? contextId = null, string? activationId = null)
    {
        await _contextJournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<ContextKey, ContextJournal> journals =
                await LoadContextJournalsAsync(sessionId, cancellationToken).ConfigureAwait(false);
            ContextKey key = Key(sessionId, contextId, activationId);
            ContextJournal journal = journals.GetValueOrDefault(key) ?? new ContextJournal(null, null);
            int start = journal.ToolPayloads?.Entries ?? 0;
            if (checkpoint.Entries <= start)
            {
                throw new InvalidDataException("Tool payload decisions must append to the existing history prefix.");
            }

            AgentToolPayloadDelta delta = new(start, checkpoint.Entries, checkpoint.PrefixHash,
                checkpoint.Payloads.Where(pair => pair.Key >= start)
                    .ToDictionary(pair => pair.Key, pair => pair.Value))
            {
                ContextId = key.ContextId,
                ActivationId = key.ActivationId
            };
            cancellationToken.ThrowIfCancellationRequested();
            // Once admitted, settle the log record even if the model request is cancelled. The
            // journal is authoritative; the snapshot is only a convenience for inspection.
            long sequence = await AppendLogAsync(sessionId, ToolPayloadLogKind, JsonSerializer.Serialize(delta),
                CancellationToken.None).ConfigureAwait(false);
            journals[key] = journal with
            {
                ToolPayloads = checkpoint with { ContextId = key.ContextId, ActivationId = key.ActivationId },
                Sequence = sequence
            };
            _contextJournals[sessionId] = journals;
            await WriteAtomicAsync(SnapshotPath(sessionId, key, "tool-payloads.json"),
                JsonSerializer.Serialize(checkpoint with
                {
                    ContextId = key.ContextId, ActivationId = key.ActivationId
                }),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _contextJournalGate.Release();
        }
    }

    /// <summary>Reads the model-prefix checkpoint; original history remains in context.json and the journal.</summary>
    public async Task<AgentContextCheckpoint?> ReadCompactionAsync(string sessionId,
        CancellationToken cancellationToken, string? contextId = null, string? activationId = null)
    {
        await _contextJournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<ContextKey, ContextJournal> journals =
                await LoadContextJournalsAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return journals.GetValueOrDefault(Key(sessionId, contextId, activationId))?.Compaction;
        }
        finally
        {
            _contextJournalGate.Release();
        }
    }

    public async Task WriteCompactionAsync(string sessionId, AgentContextCheckpoint checkpoint,
        CancellationToken cancellationToken, string? contextId = null, string? activationId = null)
    {
        await _contextJournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<ContextKey, ContextJournal> journals =
                await LoadContextJournalsAsync(sessionId, cancellationToken).ConfigureAwait(false);
            ContextKey key = Key(sessionId, contextId, activationId);
            ContextJournal journal = journals.GetValueOrDefault(key) ?? new ContextJournal(null, null);
            AgentContextCheckpoint scoped = checkpoint with
            {
                ContextId = key.ContextId, ActivationId = key.ActivationId
            };
            string json = JsonSerializer.Serialize(scoped);
            cancellationToken.ThrowIfCancellationRequested();
            long sequence = await AppendLogAsync(sessionId, CompactionLogKind, json, CancellationToken.None)
                .ConfigureAwait(false);
            journals[key] = journal with { Compaction = scoped, Sequence = sequence };
            _contextJournals[sessionId] = journals;
            await WriteAtomicAsync(SnapshotPath(sessionId, key, "compaction.json"), json,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _contextJournalGate.Release();
        }
    }

    /// <summary>Returns the most recent settled projections for sibling activations of one context.</summary>
    public async Task<(AgentToolPayloadCheckpoint? ToolPayloads, AgentContextCheckpoint? Compaction)>
        ReadLatestContextCheckpointsAsync(string sessionId, string contextId, string activationId,
            CancellationToken cancellationToken)
    {
        await _contextJournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<ContextKey, ContextJournal> journals =
                await LoadContextJournalsAsync(sessionId, cancellationToken).ConfigureAwait(false);
            KeyValuePair<ContextKey, ContextJournal>[] siblings = journals
                .Where(pair => pair.Key.ContextId == contextId && pair.Key.ActivationId != activationId)
                .OrderByDescending(pair => pair.Value.Sequence).ToArray();
            AgentToolPayloadCheckpoint? payloads = siblings
                .Where(pair => pair.Value.ToolPayloads is not null)
                .Select(pair => pair.Value.ToolPayloads).FirstOrDefault();
            AgentContextCheckpoint? compaction = siblings
                .Where(pair => pair.Value.Compaction is not null)
                .Select(pair => pair.Value.Compaction).FirstOrDefault();
            return (payloads, compaction);
        }
        finally
        {
            _contextJournalGate.Release();
        }
    }

    private async Task<Dictionary<ContextKey, ContextJournal>> LoadContextJournalsAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        if (_contextJournals.TryGetValue(sessionId, out Dictionary<ContextKey, ContextJournal>? cached))
        {
            return cached;
        }

        Dictionary<ContextKey, ContextJournal> journals = [];
        foreach (AgentSessionLogEntry entry in await ReadLogAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Kind == ToolPayloadLogKind)
            {
                AgentToolPayloadDelta delta = JsonSerializer.Deserialize<AgentToolPayloadDelta>(entry.Payload) ??
                                              throw new InvalidDataException("Invalid tool payload projection record.");
                ContextKey key = Key(sessionId, delta.ContextId, delta.ActivationId);
                ContextJournal journal = journals.GetValueOrDefault(key) ?? new ContextJournal(null, null);
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
                    {
                        ContextId = key.ContextId,
                        ActivationId = key.ActivationId
                    },
                    Sequence = entry.Seq
                };
                journals[key] = journal;
            }
            else if (entry.Kind == CompactionLogKind)
            {
                AgentContextCheckpoint checkpoint =
                    JsonSerializer.Deserialize<AgentContextCheckpoint>(entry.Payload) ??
                    throw new InvalidDataException("Invalid context compaction record.");
                ContextKey key = Key(sessionId, checkpoint.ContextId, checkpoint.ActivationId);
                ContextJournal journal = journals.GetValueOrDefault(key) ?? new ContextJournal(null, null);
                journals[key] = journal with
                {
                    Compaction = checkpoint with { ContextId = key.ContextId, ActivationId = key.ActivationId },
                    Sequence = entry.Seq
                };
            }
        }

        _contextJournals[sessionId] = journals;
        return journals;
    }

    private static ContextKey Key(string sessionId, string? contextId, string? activationId)
    {
        return new ContextKey(string.IsNullOrWhiteSpace(contextId) ? sessionId : contextId,
            string.IsNullOrWhiteSpace(activationId) ? "main" : activationId);
    }

    private string SnapshotPath(string sessionId, ContextKey key, string fileName)
    {
        string directory = ResolveSessionDirectory(sessionId);
        if (key.ContextId == sessionId && key.ActivationId == "main")
        {
            return Path.Combine(directory, fileName);
        }

        string hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(key.ContextId + "\n" + key.ActivationId))).ToLowerInvariant();
        return Path.Combine(directory, "context-state", hash, fileName);
    }
}
