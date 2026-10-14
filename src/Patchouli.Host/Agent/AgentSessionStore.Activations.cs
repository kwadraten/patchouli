using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Patchouli.Host.Agent;

public sealed partial class AgentSessionStore
{
    private const string ActivationEnvelopeLogKind = "context/activation-envelope";
    private readonly SemaphoreSlim _activationEnvelopeGate = new(1, 1);
    private readonly Dictionary<string, AgentActivationEnvelope> _activationEnvelopes = new(StringComparer.Ordinal);

    /// <summary>Persists the first model request contract, or reports a changed contract on recovery.</summary>
    public async Task<string?> EnsureActivationEnvelopeAsync(string sessionId, AgentActivationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(envelope);
        await _activationEnvelopeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string key = ActivationKey(sessionId, envelope.ContextId, envelope.ActivationId);
            AgentActivationEnvelope? saved = _activationEnvelopes.GetValueOrDefault(key);
            if (saved is null)
            {
                foreach (AgentSessionLogEntry entry in await ReadLogAsync(sessionId, cancellationToken)
                             .ConfigureAwait(false))
                {
                    if (entry.Kind != ActivationEnvelopeLogKind)
                    {
                        continue;
                    }

                    AgentActivationEnvelope candidate =
                        JsonSerializer.Deserialize<AgentActivationEnvelope>(entry.Payload)
                        ?? throw new InvalidDataException("Invalid activation request envelope.");
                    string candidateKey = ActivationKey(sessionId, candidate.ContextId, candidate.ActivationId);
                    _activationEnvelopes[candidateKey] = candidate;
                }

                saved = _activationEnvelopes.GetValueOrDefault(key);
            }

            if (saved is not null)
            {
                return saved.EnvelopeHash == envelope.EnvelopeHash
                    ? null
                    : "AGENT_ACTIVATION_DIVERGED: the frozen model, instructions or tool schemas changed for this activation.";
            }

            cancellationToken.ThrowIfCancellationRequested();
            string json = JsonSerializer.Serialize(envelope);
            await AppendLogAsync(sessionId, ActivationEnvelopeLogKind, json, CancellationToken.None)
                .ConfigureAwait(false);
            _activationEnvelopes[key] = envelope;
            string hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(envelope.ContextId + "\n" + envelope.ActivationId))).ToLowerInvariant();
            string path = Path.Combine(ResolveSessionDirectory(sessionId), "activations", hash, "envelope.json");
            await WriteAtomicAsync(path, json, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        finally
        {
            _activationEnvelopeGate.Release();
        }
    }

    private static string ActivationKey(string sessionId, string contextId, string activationId)
    {
        return sessionId + "\0" + contextId + "\0" + activationId;
    }
}
