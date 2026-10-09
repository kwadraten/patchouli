using System.Text;

namespace Patchouli.Llm;

/// <summary>Role of a message in the deterministic conversation prefix.</summary>
public enum LlmChatRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>One content part of a chat message. Parts are immutable: the prefix must stay byte-stable.</summary>
public abstract record LlmMessagePart
{
    /// <summary>Plain UTF-8 text.</summary>
    public sealed record LlmTextPart(string Text) : LlmMessagePart;

    public sealed record LlmToolCallPart(string Id, string Name, string Arguments, string Metadata = "")
        : LlmMessagePart;

    public sealed record LlmAssistantMetadataPart(string Metadata) : LlmMessagePart;

    public sealed record LlmToolResultPart(string CallId, string Name, string Content, bool IsError) : LlmMessagePart;

    /// <summary>
    /// An image passed to a vision model. <paramref name="DataUriOrUrl"/> is either an <c>https</c> URL or a
    /// <c>data:image/...;base64,...</c> data URI; local file paths are deliberately not accepted, because a
    /// provider call must not depend on host paths (ADR 0010 data boundary).
    /// </summary>
    public sealed record LlmImagePart(string DataUriOrUrl, string MimeType, string Detail) : LlmMessagePart;
}

/// <summary>
/// An immutable chat message. Everything a request sends is one of these, which is what makes "the prefix
/// never changes" checkable instead of a convention.
/// </summary>
/// <remarks>
/// Record equality on this type compares the <see cref="Parts"/> list by reference, because a part list is a
/// reference type. Compare two messages structurally through <see cref="LlmChatHistory.CanAppend"/> (or
/// <see cref="LlmMessageContentComparer.IsSamePart"/> for a single part), never with <c>==</c>.
/// </remarks>
public sealed record LlmChatMessage(LlmChatRole Role, IReadOnlyList<LlmMessagePart> Parts)
{
    /// <summary>Creates a single-part text message.</summary>
    public static LlmChatMessage Text(LlmChatRole role, string text)
    {
        return new LlmChatMessage(role, [new LlmMessagePart.LlmTextPart(text)]);
    }

    public static LlmChatMessage System(string text)
    {
        return Text(LlmChatRole.System, text);
    }

    public static LlmChatMessage User(string text)
    {
        return Text(LlmChatRole.User, text);
    }

    public static LlmChatMessage Assistant(string text)
    {
        return Text(LlmChatRole.Assistant, text);
    }

    public static LlmChatMessage Tool(string text)
    {
        return Text(LlmChatRole.Tool, text);
    }

    /// <summary>True when the message carries at least one part.</summary>
    public bool HasContent => Parts.Count > 0;

    /// <summary>True when every part is text. Image parts only travel through the vision call.</summary>
    public bool IsTextOnly => Parts.All(part => part is LlmMessagePart.LlmTextPart);
}

/// <summary>
/// An append-only chat history: immutable system instructions, immutable tool definitions and an
/// append-only message list.
/// </summary>
/// <remarks>
/// <para><b>Provider prefix-cache invariant (ADR 0036).</b> Session history is append-only. System
/// instructions, tool definitions and existing messages stay byte-stable and in stable order; new content
/// is only ever appended at the end; after resume the history is rebuilt in its recorded order. Cache
/// expiry therefore changes only cost and latency, never behaviour.</para>
/// <para><b>API usage constraint.</b> This type has no API that rewrites index <c>i</c> of an existing
/// entry, no API that reorders, truncates or summarizes, and no setter that could mutate an entry in place.
/// <see cref="Append(IReadOnlyList{LlmChatMessage})"/> only accepts a list whose leading entries equal the
/// current messages, so a caller cannot use it to replace history by accident. Any future feature that
/// must rewrite or reorder history requires an ADR, not a convenience patch here.</para>
/// </remarks>
public sealed class LlmChatHistory
{
    private readonly List<LlmChatMessage> _messages;

    private LlmChatHistory(string instructions, IReadOnlyList<string> toolDefinitions,
        IEnumerable<LlmChatMessage> messages, int revision)
    {
        Instructions = instructions;
        ToolDefinitions = [.. toolDefinitions];
        _messages = [.. messages];
        Revision = revision;
    }

    /// <summary>Creates an empty history with stable instructions and tool definitions.</summary>
    public static LlmChatHistory Create(string instructions,
        IReadOnlyList<string>? toolDefinitions = null)
    {
        return new LlmChatHistory(instructions, toolDefinitions ?? [], [], 0);
    }

    /// <summary>
    /// Instructions that form the head of the deterministic prefix. Never changed after creation, including
    /// across resume; a different instruction set is a different session, not an edit.
    /// </summary>
    public string Instructions { get; }

    /// <summary>
    /// Tool definitions in their stable order. Never changed after creation, for the same reason as
    /// <see cref="Instructions"/>.
    /// </summary>
    public IReadOnlyList<string> ToolDefinitions { get; }

    /// <summary>Number of successful appends; monotonic and useful for the session event sequence.</summary>
    public int Revision { get; }

    /// <summary>Messages in recorded order. The returned list is a snapshot and cannot mutate this history.</summary>
    public IReadOnlyList<LlmChatMessage> Messages => [.. _messages];

    /// <summary>Number of messages currently in the history.</summary>
    public int Count => _messages.Count;

    /// <summary>
    /// True when <paramref name="entries"/> is a valid append: it must still contain the recorded entries
    /// value-for-value in order (so nothing is rewritten, reordered or dropped) and every entry beyond the
    /// recorded prefix must carry content.
    /// </summary>
    public bool CanAppend(IReadOnlyList<LlmChatMessage> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!IsPrefixOfRecorded(entries))
        {
            return false;
        }

        for (int index = _messages.Count; index < entries.Count; index++)
        {
            if (entries[index] is not { HasContent: true })
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="entry"/> is exactly the message already recorded at its position, so
    /// re-appending a rebuilt log entry is a no-op rather than a rewrite. An empty history accepts any entry
    /// with content.
    /// </summary>
    public bool CanAppendMessage(LlmChatMessage entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _messages.Count == 0 ? entry.HasContent : IsSameMessage(_messages[0], entry);
    }

    /// <summary>
    /// Appends the tail of <paramref name="entries"/>: the recorded entries must still match value-for-value
    /// and earlier positions, so only genuinely new content is added at the end. This is the only mutation
    /// this type offers, which is what makes the ADR 0036 prefix cache invariant structural. It also supports
    /// resume: a caller replays the recorded log in its recorded order and the history rebuilds identically.
    /// </summary>
    /// <exception cref="LlmChatException">
    /// Thrown with <see cref="LlmFailureCodes.HistoryInvariantViolated"/> when the incoming list would change
    /// an existing entry, reorder the prefix, drop entries or append an empty message.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when the list or one of its entries is null.</exception>
    public LlmChatHistory Append(IReadOnlyList<LlmChatMessage> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!CanAppend(entries))
        {
            // A null entry is a programming error, not an invariant violation; report it as such so a
            // caller's argument bug is never mistaken for a history bug.
            foreach (LlmChatMessage entry in entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
            }

            throw new LlmChatException(LlmFailureCodes.HistoryInvariantViolated, DescribeRejection(entries));
        }

        if (entries.Count == _messages.Count)
        {
            return this;
        }

        List<LlmChatMessage> appended = [.. _messages];
        appended.AddRange(entries.Skip(_messages.Count));
        return new LlmChatHistory(Instructions, ToolDefinitions, appended, Revision + 1);
    }

    /// <summary>
    /// A stable hash of the deterministic prefix (instructions, tool definitions, message count and every
    /// message's role and parts). Callers can log it to detect a prefix change before it costs a cache miss.
    /// </summary>
    public string ComputePrefixSignature()
    {
        StringBuilder builder = new();
        builder.Append("instructions:").Append(Instructions).Append('\u001f');
        builder.Append("tools:").Append(string.Join('\u001e', ToolDefinitions)).Append('\u001f');
        builder.Append("count:").Append(_messages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append('\u001f');
        foreach (LlmChatMessage message in _messages)
        {
            builder.Append((int)message.Role).Append('=');
            foreach (LlmMessagePart part in message.Parts)
            {
                switch (part)
                {
                    case LlmMessagePart.LlmTextPart text:
                        builder.Append("t:").Append(text.Text);
                        break;
                    case LlmMessagePart.LlmImagePart image:
                        builder.Append("i:").Append(image.MimeType).Append(':').Append(image.Detail).Append(':')
                            .Append(image.DataUriOrUrl);
                        break;
                    case LlmMessagePart.LlmToolCallPart call:
                        builder.Append("call:").Append(call.Id).Append(':').Append(call.Name).Append(':')
                            .Append(call.Arguments).Append(':').Append(call.Metadata);
                        break;
                    case LlmMessagePart.LlmAssistantMetadataPart meta:
                        builder.Append("meta:").Append(meta.Metadata);
                        break;
                    case LlmMessagePart.LlmToolResultPart result:
                        builder.Append("result:").Append(result.CallId).Append(':').Append(result.Name).Append(':')
                            .Append(result.IsError).Append(':').Append(result.Content);
                        break;
                    default:
                        builder.Append("?:");
                        break;
                }

                builder.Append('\u001d');
            }

            builder.Append('\u001e');
        }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private int IndexOfConflict(IReadOnlyList<LlmChatMessage> entries)
    {
        for (int index = 0; index < entries.Count && index < _messages.Count; index++)
        {
            if (entries[index] is not { } entry || !IsSameMessage(_messages[index], entry))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// True when every recorded entry is still present, value-for-value, at the same position in
    /// <paramref name="entries"/>. A null entry never matches.
    /// </summary>
    private bool IsPrefixOfRecorded(IReadOnlyList<LlmChatMessage> entries)
    {
        if (entries.Count < _messages.Count)
        {
            return false;
        }

        for (int index = 0; index < _messages.Count; index++)
        {
            if (entries[index] is not { } entry || !IsSameMessage(_messages[index], entry))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Explains exactly which part of the append-only invariant the incoming list would break.</summary>
    private string DescribeRejection(IReadOnlyList<LlmChatMessage> entries)
    {
        if (entries.Count < _messages.Count)
        {
            return $"Chat history is append-only: a list of {entries.Count} entries would drop " +
                   $"{_messages.Count - entries.Count} recorded message(s). Truncation breaks the provider " +
                   "prefix cache (ADR 0036).";
        }

        int conflict = IndexOfConflict(entries);
        if (conflict >= 0)
        {
            return $"Chat history is append-only: entry {conflict} differs from the recorded content at the " +
                   "same position. Rewriting or reordering the prefix breaks the provider prefix cache " +
                   "(ADR 0036).";
        }

        return "Chat history is append-only: a new entry must carry at least one content part.";
    }

    /// <summary>
    /// Structural comparison of two history entries. Written explicitly rather than relying on record
    /// equality, because a part list is a reference type and the append-only check must never depend on that.
    /// </summary>
    private static bool IsSameMessage(LlmChatMessage left, LlmChatMessage right)
    {
        if (left.Role != right.Role || left.Parts.Count != right.Parts.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Parts.Count; index++)
        {
            if (!LlmMessageContentComparer.IsSamePart(left.Parts[index], right.Parts[index]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Value comparison for the content of one message. This exists because the generated equality of the part
/// records (defined inside an abstract record) is not the contract the prefix invariant needs; the
/// append-only checks compare parts explicitly through this type instead.
/// </summary>
public static class LlmMessageContentComparer
{
    /// <summary>True when two content parts carry the same value.</summary>
    public static bool IsSamePart(LlmMessagePart left, LlmMessagePart right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return (left, right) switch
        {
            (LlmMessagePart.LlmToolCallPart a, LlmMessagePart.LlmToolCallPart b) => a == b,
            (LlmMessagePart.LlmAssistantMetadataPart a, LlmMessagePart.LlmAssistantMetadataPart b) => a == b,
            (LlmMessagePart.LlmToolResultPart a, LlmMessagePart.LlmToolResultPart b) => a == b,
            (LlmMessagePart.LlmTextPart a, LlmMessagePart.LlmTextPart b) =>
                string.Equals(a.Text, b.Text, StringComparison.Ordinal),
            (LlmMessagePart.LlmImagePart a, LlmMessagePart.LlmImagePart b) =>
                string.Equals(a.DataUriOrUrl, b.DataUriOrUrl, StringComparison.Ordinal) &&
                string.Equals(a.MimeType, b.MimeType, StringComparison.Ordinal) &&
                string.Equals(a.Detail, b.Detail, StringComparison.Ordinal),
            _ => false
        };
    }
}
