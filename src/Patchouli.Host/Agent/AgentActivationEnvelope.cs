using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Host.Agent;

/// <summary>The exact host-owned model request contract frozen for one context activation.</summary>
public sealed record AgentActivationEnvelope(
    string ContextId,
    string ActivationId,
    string Instructions,
    IReadOnlyList<string> ToolDefinitions,
    string ModelSelection,
    string ConfigurationKeyHash,
    int? MaxTokens,
    string EnvelopeHash)
{
    public static AgentActivationEnvelope Create(AgentEffectContext context, string configurationKey,
        int? maxTokens)
    {
        ArgumentNullException.ThrowIfNull(context);
        byte[] configHash = SHA256.HashData(Encoding.UTF8.GetBytes(configurationKey));
        var identity = new
        {
            context.ContextId,
            context.ActivationId,
            context.Instructions,
            context.ToolDefinitions,
            ModelSelection = context.ModelSelection is { } selection
                ? ModelSelectionCodec.Encode(selection)
                : "",
            ConfigurationKeyHash = Convert.ToHexString(configHash),
            MaxTokens = maxTokens
        };
        string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)));
        return new AgentActivationEnvelope(identity.ContextId, identity.ActivationId, identity.Instructions,
            identity.ToolDefinitions, identity.ModelSelection, identity.ConfigurationKeyHash, identity.MaxTokens, hash);
    }
}
