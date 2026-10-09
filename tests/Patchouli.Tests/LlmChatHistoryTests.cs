using FluentAssertions;
using Patchouli.Llm;

namespace Patchouli.Tests;

/// <summary>
/// Covers the API misuse guards that make the ADR 0036 append-only invariant structural rather than a
/// convention: there is deliberately no API here that can rewrite, reorder or truncate history.
/// </summary>
public sealed class LlmChatHistoryTests
{
    [Fact]
    public void The_only_public_surface_is_creation_append_and_inspection()
    {
        string[] methods = typeof(LlmChatHistory).GetMethods()
            .Where(method => method.IsPublic && method.DeclaringType == typeof(LlmChatHistory) &&
                             !method.IsSpecialName)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        methods.Should().Equal("Append", "CanAppend", "CanAppendMessage", "ComputePrefixSignature", "Create");
        typeof(LlmChatHistory).GetProperties().Where(property => property.CanWrite)
            .Should().BeEmpty("history properties must not be settable");

        // Messages and parts are positional records, so the compiler emits an init setter for each parameter.
        // The compatible invariant is that a message can never be mutated in place: its parts are handed out
        // as a collection the caller cannot write through, and the history keeps its own copy.
        LlmChatMessage message = LlmChatMessage.User("one");
        Action mutateParts = () => ((IList<LlmMessagePart>)message.Parts).Clear();
        mutateParts.Should().Throw<NotSupportedException>();
        message.Parts.Should().HaveCount(1);
    }

    [Fact]
    public void An_equal_message_value_may_be_rebuilt_and_reappended()
    {
        // Record equality on LlmChatMessage compares the part list by reference, so re-appending a message
        // reconstructed from the log must still be accepted: the values at each position are identical.
        LlmChatHistory history = LlmChatHistory.Create("system").Append([LlmChatMessage.User("one")]);

        LlmChatHistory extended = history.Append([LlmChatMessage.User("one"), LlmChatMessage.User("two")]);

        extended.Count.Should().Be(2);
        extended.CanAppend([LlmChatMessage.User("one"), LlmChatMessage.User("two")]).Should().BeTrue();
    }

    [Fact]
    public void Instructions_and_tool_definitions_are_fixed_for_the_life_of_the_history()
    {
        LlmChatHistory history = LlmChatHistory.Create("system", ["a", "b"]);
        LlmChatHistory appended = history.Append([LlmChatMessage.User("one")]);

        appended.Instructions.Should().Be("system");
        appended.ToolDefinitions.Should().Equal("a", "b");
        history.Count.Should().Be(0);
        history.Revision.Should().Be(0);
        appended.Revision.Should().Be(1);
    }

    [Fact]
    public void Replaying_a_recorded_log_rebuilds_the_identical_history()
    {
        LlmChatMessage[] log =
        [
            LlmChatMessage.User("page 1"),
            LlmChatMessage.Assistant("translated 1"),
            LlmChatMessage.User("page 2")
        ];
        LlmChatHistory history = LlmChatHistory.Create("system");
        for (int index = 0; index < log.Length; index++)
        {
            history = history.Append(log[..(index + 1)]);
        }

        // Resume replays the whole recorded list in one call: Append accepts a list whose leading entries
        // already match the recorded prefix and only the tail is new.
        LlmChatHistory replayed = LlmChatHistory.Create("system").Append(log);

        history.Count.Should().Be(3);
        replayed.Count.Should().Be(3);
        replayed.ComputePrefixSignature().Should().Be(history.ComputePrefixSignature());
        LlmChatHistory extended = history.Append([.. log, LlmChatMessage.User("page 3")]);
        extended.Count.Should().Be(4);
    }

    [Fact]
    public void Reappending_already_recorded_messages_is_a_no_op()
    {
        LlmChatHistory history = LlmChatHistory.Create("system").Append([LlmChatMessage.User("one")]);

        LlmChatHistory same = history.Append(history.Messages);

        same.Count.Should().Be(1);
        same.ComputePrefixSignature().Should().Be(history.ComputePrefixSignature());
    }

    [Fact]
    public void Rewriting_a_recorded_message_is_rejected()
    {
        LlmChatHistory history = LlmChatHistory.Create("system").Append([LlmChatMessage.User("one")]);

        Action act = () => history.Append([LlmChatMessage.User("one changed"), LlmChatMessage.User("two")]);

        act.Should().Throw<LlmChatException>()
            .Which.ErrorCode.Should().Be(LlmFailureCodes.HistoryInvariantViolated);
        history.Count.Should().Be(1);
    }

    [Fact]
    public void Reordering_recorded_messages_is_rejected()
    {
        LlmChatHistory history = LlmChatHistory.Create("system")
            .Append(
            [
                LlmChatMessage.User("one"),
                LlmChatMessage.Assistant("two")
            ]);

        Action act = () => history.Append([LlmChatMessage.Assistant("two"), LlmChatMessage.User("one")]);

        act.Should().Throw<LlmChatException>()
            .Which.ErrorCode.Should().Be(LlmFailureCodes.HistoryInvariantViolated);
    }

    [Fact]
    public void An_empty_new_message_is_rejected()
    {
        LlmChatHistory history = LlmChatHistory.Create("system");

        history.CanAppend([new LlmChatMessage(LlmChatRole.User, [])]).Should().BeFalse();
        history.CanAppendMessage(new LlmChatMessage(LlmChatRole.User, [])).Should().BeFalse();
        Action act = () => history.Append([new LlmChatMessage(LlmChatRole.User, [])]);

        act.Should().Throw<LlmChatException>()
            .Which.ErrorCode.Should().Be(LlmFailureCodes.HistoryInvariantViolated);
    }

    [Fact]
    public void A_new_message_with_content_is_appendable_as_part_of_a_rebuilt_list()
    {
        LlmChatHistory history = LlmChatHistory.Create("system").Append([LlmChatMessage.User("one")]);

        // CanAppendMessage answers "is this already the recorded entry", which is what makes re-appending a
        // rebuilt log a no-op. New content is appended through the list overload, which carries the prefix.
        history.CanAppendMessage(LlmChatMessage.User("one")).Should().BeTrue();
        history.CanAppendMessage(LlmChatMessage.User("two")).Should().BeFalse();
        history.CanAppendMessage(LlmChatMessage.Assistant("one")).Should().BeFalse();
        history.CanAppend([LlmChatMessage.User("one"), LlmChatMessage.User("two")]).Should().BeTrue();
        LlmChatHistory extended = history.Append([LlmChatMessage.User("one"), LlmChatMessage.User("two")]);

        extended.Count.Should().Be(2);
        extended.CanAppendMessage(LlmChatMessage.User("one")).Should().BeTrue();
    }

    [Fact]
    public void CanAppend_describes_exactly_what_Append_accepts()
    {
        LlmChatHistory history = LlmChatHistory.Create("system").Append([LlmChatMessage.User("one")]);
        LlmChatMessage[] legal = [LlmChatMessage.User("one"), LlmChatMessage.User("two")];
        LlmChatMessage[] illegal = [LlmChatMessage.User("one changed")];

        history.CanAppend(legal).Should().BeTrue();
        history.CanAppend(illegal).Should().BeFalse();
        Action accept = () => history.Append(legal);
        Action reject = () => history.Append(illegal);
        accept.Should().NotThrow();
        reject.Should().Throw<LlmChatException>();
    }

    [Fact]
    public void The_messages_snapshot_is_a_copy_that_cannot_mutate_the_history()
    {
        LlmChatHistory history = LlmChatHistory.Create("system").Append([LlmChatMessage.User("one")]);

        IReadOnlyList<LlmChatMessage> snapshot = history.Messages;

        snapshot.Should().HaveCount(1);
        Action mutate = () => ((IList<LlmChatMessage>)snapshot).Clear();
        mutate.Should().Throw<NotSupportedException>();
        history.Count.Should().Be(1);
        history.Messages.Should().HaveCount(1);
    }

    [Fact]
    public void The_prefix_signature_changes_only_when_the_prefix_changes()
    {
        LlmChatHistory baseHistory = LlmChatHistory.Create("system", ["tool"]).Append([LlmChatMessage.User("one")]);
        string signature = baseHistory.ComputePrefixSignature();

        baseHistory.ComputePrefixSignature().Should().Be(signature);
        baseHistory.Append([LlmChatMessage.User("one"), LlmChatMessage.User("two")])
            .ComputePrefixSignature().Should().NotBe(signature);
        LlmChatHistory.Create("system other", ["tool"]).Append([LlmChatMessage.User("one")])
            .ComputePrefixSignature().Should().NotBe(signature);
        LlmChatHistory.Create("system", ["tool", "extra"]).Append([LlmChatMessage.User("one")])
            .ComputePrefixSignature().Should().NotBe(signature);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        LlmChatHistory history = LlmChatHistory.Create("system");

        Action appendNull = () => history.Append((IReadOnlyList<LlmChatMessage>)null!);
        Action appendNullEntry = () => history.Append([null!]);
        Action canAppendNull = () => history.CanAppend(null!);

        appendNull.Should().Throw<ArgumentNullException>();
        appendNullEntry.Should().Throw<ArgumentNullException>();
        canAppendNull.Should().Throw<ArgumentNullException>();
        history.CanAppend([null!]).Should().BeFalse();
    }
}
