using System.Text.Json;
using System.Text.Encodings.Web;
using LlmTornado;
using LlmTornado.Codex;
using Patchouli.Core.Credentials;

namespace Patchouli.Llm;

/// <summary>Text inference through LLMTornado's subscription protocol; Patchouli retains ownership of its agent loop.</summary>
public sealed class LlmTornadoSubscriptionTransport(
    ICredentialStore store,
    Func<ICodexOAuthCredentialStore, CodexOAuthOptions>? optionsFactory = null) : ILlmChatTransport
{
    // The transcript is JSON HTTP content, never HTML. Keep source text readable for the model.
    private static readonly JsonSerializerOptions TranscriptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (LlmSubscriptionCatalog.Find(request.Provider.ProviderId)?.Backend != "codex-oauth")
        {
            return LlmTransportResult.Failure(LlmFailureCodes.BadEndpointConfig, "Unsupported subscription backend.");
        }

        if (request.Messages.Count == 0 || request.Messages.Any(message =>
                message.Parts.Any(part => part is LlmMessagePart.LlmImagePart)))
        {
            return LlmTransportResult.Failure(LlmFailureCodes.UnsupportedInput,
                "This subscription backend accepts text only.");
        }

        if (request.Temperature is not null || request.MaxTokens is not null)
        {
            return LlmTransportResult.Failure(LlmFailureCodes.UsageContractViolated,
                "The subscription protocol does not expose temperature or max-token overrides.");
        }

        SemaphoreSlim gate = LlmSubscriptionAccess.For(store);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ICodexOAuthCredentialStore credentials = new LlmCodexCredentialStore(store, request.Provider.ProviderId);
            CodexOAuthOptions options = optionsFactory?.Invoke(credentials) ??
                                        new CodexOAuthOptions { CredentialStore = credentials };
            options.CredentialStore = credentials;
            await using CodexOAuthSession session = await new TornadoApi().Codex
                .ConnectOAuthAsync(options, cancellationToken)
                .ConfigureAwait(false);
            string hostInstructions = string.Join("\n\n",
                request.Messages.Where(message => message.Role == LlmChatRole.System)
                    .Select(message =>
                        string.Concat(message.Parts.Cast<LlmMessagePart.LlmTextPart>().Select(part => part.Text))));
            List<CodexOAuthHistoryItem> initial = [];
            LlmTransportMessage? promptMessage =
                request.Messages.LastOrDefault() is { Role: LlmChatRole.User } user ? user : null;
            foreach (LlmTransportMessage message in
                     request.Messages.Where(message => message.Role != LlmChatRole.System))
            {
                if (ReferenceEquals(message, promptMessage))
                {
                    continue;
                }

                string text =
                    string.Concat(message.Parts.OfType<LlmMessagePart.LlmTextPart>().Select(part => part.Text));
                if (text.Length > 0)
                {
                    initial.Add(message.Role == LlmChatRole.Assistant
                        ? CodexOAuthHistoryItem.AssistantMessage(text)
                        : CodexOAuthHistoryItem.UserMessage(text));
                }

                foreach (LlmMessagePart.LlmToolCallPart call in message.Parts.OfType<LlmMessagePart.LlmToolCallPart>())
                {
                    initial.Add(CodexOAuthHistoryItem.FunctionCall(call.Id, call.Name, call.Arguments));
                }

                foreach (LlmMessagePart.LlmToolResultPart result in message.Parts
                             .OfType<LlmMessagePart.LlmToolResultPart>())
                {
                    initial.Add(CodexOAuthHistoryItem.FunctionOutput(result.CallId, result.Content));
                }
            }

            CodexOAuthThread thread = await session.StartThreadAsync(new CodexOAuthThreadOptions
            {
                Model = request.Model, Instructions = hostInstructions, InitialHistory = initial
            }, cancellationToken).ConfigureAwait(false);
            string input = promptMessage is null
                ? "Continue from the recorded function results."
                : string.Concat(promptMessage.Parts.OfType<LlmMessagePart.LlmTextPart>().Select(part => part.Text));
            CodexOAuthTurnOptions turnOptions = new()
            {
                Tools = request.ToolDefinitions.Select(definition =>
                    Newtonsoft.Json.JsonConvert.DeserializeObject<LlmTornado.Common.Tool>(definition)!).ToArray()
            };
            // No SDK tool handler: the host's F# controller owns tool execution and turn boundaries.
            CodexOAuthTurnResult turn =
                await thread.RunAsync(input, turnOptions, cancellationToken).ConfigureAwait(false);
            if (turn.Status is not (null or "completed"))
            {
                return LlmTransportResult.Failure(LlmFailureCodes.TemporaryProviderError,
                    "The subscription model did not complete the turn.");
            }

            Newtonsoft.Json.Linq.JToken? usage = turn.Response["usage"];
            int inputTokens = usage?.Value<int?>("input_tokens") ?? 0;
            int output = usage?.Value<int?>("output_tokens") ?? 0;
            int? cached = usage?["input_tokens_details"]?.Value<int?>("cached_tokens");
            return LlmTransportResult.Success(turn.FinalResponse, thread.Model, turn.Status,
                    new LlmUsage(inputTokens, output, usage?.Value<int?>("total_tokens") ?? inputTokens + output,
                        cached, null)) with
                {
                    ToolCalls = turn.ToolCalls.Select(call => new LlmToolCall(call.Id ?? "",
                        call.FunctionCall?.Name ?? "", call.FunctionCall?.Arguments ?? "")).ToArray()
                };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LlmTransportResult.Failure(LlmFailureCodes.NetworkTimeout, "The subscription request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CodexOAuthException exception)
        {
            string code = exception.StatusCode switch
            {
                401 or 403 => LlmFailureCodes.AuthFailed,
                429 => LlmFailureCodes.RateLimited,
                404 => LlmFailureCodes.ModelNotFound,
                >= 500 => LlmFailureCodes.TemporaryProviderError,
                _ when LlmFailureMapper.IsContextLengthExceeded(exception.Message) =>
                    LlmFailureCodes.ContextLengthExceeded,
                _ when exception.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) =>
                    LlmFailureCodes.AuthFailed,
                _ when exception.Message.Contains("model", StringComparison.OrdinalIgnoreCase) => LlmFailureCodes
                    .ModelNotFound,
                _ => LlmFailureCodes.UnknownProviderError
            };
            return LlmTransportResult.Failure(code,
                "Subscription request failed. Check the login and refresh the available models.");
        }
        catch (HttpRequestException)
        {
            return LlmTransportResult.Failure(LlmFailureCodes.TemporaryProviderError,
                "The subscription network connection failed.");
        }
        catch (InvalidOperationException)
        {
            return LlmTransportResult.Failure(LlmFailureCodes.AuthFailed,
                "The subscription connection could not be established. Check the stored login.");
        }
        catch (Exception)
        {
            return LlmTransportResult.Failure(LlmFailureCodes.UnknownProviderError,
                "The subscription provider returned an unexpected response.");
        }
        finally
        {
            gate.Release();
        }
    }
}
