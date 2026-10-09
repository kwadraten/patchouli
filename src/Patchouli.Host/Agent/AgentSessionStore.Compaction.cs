using System.Text.Json;

namespace Patchouli.Host.Agent;

public sealed partial class AgentSessionStore
{
    /// <summary>Reads the model-prefix checkpoint; original history remains in context.json and the journal.</summary>
    public async Task<AgentContextCheckpoint?> ReadCompactionAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(ResolveSessionDirectory(sessionId), "compaction.json");
        return !File.Exists(path)
            ? null
            : JsonSerializer.Deserialize<AgentContextCheckpoint>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }

    public Task WriteCompactionAsync(string sessionId, AgentContextCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        return WriteAtomicAsync(
            Path.Combine(ResolveSessionDirectory(sessionId), "compaction.json"), JsonSerializer.Serialize(checkpoint),
            cancellationToken);
    }
}
