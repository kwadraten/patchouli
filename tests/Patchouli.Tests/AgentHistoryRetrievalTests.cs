using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Agent;
using Patchouli.Host.Agent;

namespace Patchouli.Tests;

public sealed class AgentHistoryRetrievalTests
{
    [Fact]
    public void Operation_lookup_skips_previous_plain_text_fsi_errors()
    {
        string payload = JsonSerializer.Serialize(new
        {
            sdkOperations = new[] { new { OperationId = "wanted", Payload = "original" } }
        });
        AgentEffectContext context = new("history", "", [],
        [
            HistoryEntry.NewNativeToolResult("failed", "fsi", "FSI compilation failed", true),
            HistoryEntry.NewNativeToolResult("ok", "fsi", payload, false)
        ]);
        AgentToolOutcome result = AgentHistoryTool.Read(context, """{"operationId":"wanted"}""");
        result.Succeeded.Should().BeTrue(result.Payload);
        using JsonDocument document = JsonDocument.Parse(result.Payload);
        document.RootElement.GetProperty("entries")[0].GetProperty("content").GetString().Should().Be("original");
    }

    [Fact]
    public void Search_finds_a_hidden_middle_and_returns_content_near_the_match()
    {
        string payload = new string('x', 500000) + "hidden-middle-evidence" + new string('y', 500000);
        HistoryEntry original = HistoryEntry.NewNativeToolResult("call", "fetch", payload, false);
        AgentEffectContext context = new("history", "", [], [original])
        {
            ModelHistory = [AgentToolPayloadFolder.Project(original)]
        };
        AgentToolOutcome result = AgentHistoryTool.Read(context,
            """{"query":"hidden-middle-evidence","limit":1,"maxChars":1024}""");
        result.Succeeded.Should().BeTrue(result.Payload);
        using JsonDocument document = JsonDocument.Parse(result.Payload);
        JsonElement match = document.RootElement.GetProperty("entries")[0];
        match.GetProperty("offset").GetInt32().Should().BeGreaterThan(490000);
        match.GetProperty("content").GetString().Should().Contain("hidden-middle-evidence");
        match.GetProperty("content").GetString()!.Length.Should().BeLessThanOrEqualTo(1024);
    }

    [Fact]
    public void Search_decodes_nested_unicode_sdk_payloads_and_operation_pages_restore_the_exact_original()
    {
        string original = JsonSerializer.Serialize(new { text = new string('中', 50000) + "隐藏的证据😀" });
        string fsi = JsonSerializer.Serialize(new
        {
            output = "selected headings",
            sdkOperations = new[] { new { OperationId = "effect/1/fetch/1", Payload = original } }
        });
        AgentEffectContext context = new("history", "", [],
            [HistoryEntry.NewNativeToolResult("fsi", "fsi", fsi, false)]);
        AgentToolOutcome search = AgentHistoryTool.Read(context, """{"query":"隐藏的证据","limit":1}""");
        search.Succeeded.Should().BeTrue(search.Payload);
        using (JsonDocument found = JsonDocument.Parse(search.Payload))
        {
            JsonElement match = found.RootElement.GetProperty("entries")[0].GetProperty("match");
            match.GetProperty("excerpt").GetString().Should().Contain("隐藏的证据");
            match.GetProperty("path").GetString().Should().Contain("sdkOperations");
        }

        StringBuilder restored = new();
        int offset = 0;
        do
        {
            AgentToolOutcome page = AgentHistoryTool.Read(context, JsonSerializer.Serialize(new
            {
                start = 1, offset, limit = 1, operationId = "effect/1/fetch/1"
            }));
            page.Succeeded.Should().BeTrue(page.Payload);
            using JsonDocument document = JsonDocument.Parse(page.Payload);
            restored.Append(document.RootElement.GetProperty("entries")[0].GetProperty("content").GetString());
            if (document.RootElement.GetProperty("nextStart").ValueKind == JsonValueKind.Null)
            {
                break;
            }

            int next = document.RootElement.GetProperty("nextOffset").GetInt32();
            next.Should().BeGreaterThan(offset);
            offset = next;
        } while (restored.Length <= original.Length);

        restored.ToString().Should().Be(original);
    }

    [Fact]
    public void History_honors_the_context_page_budget_and_rejects_a_split_surrogate_offset()
    {
        AgentEffectContext context = new("history", "", [], [HistoryEntry.NewUserMessage(new string('中', 10000))])
        {
            HistoryPageCharacters = 128
        };
        using JsonDocument page = JsonDocument.Parse(AgentHistoryTool.Read(context, "{}").Payload);
        page.RootElement.GetProperty("entries")[0].GetProperty("content").GetString()!.Length.Should().Be(128);
        page.RootElement.GetProperty("nextOffset").GetInt32().Should().Be(128);

        string fsi = JsonSerializer.Serialize(new
        {
            sdkOperations = new[] { new { OperationId = "emoji", Payload = "😀" } }
        });
        AgentEffectContext unicode = context with
        {
            History = [HistoryEntry.NewNativeToolResult("fsi", "fsi", fsi, false)]
        };
        AgentHistoryTool.Read(unicode, """{"start":1,"operationId":"emoji","offset":1}""").Succeeded.Should().BeFalse();
    }
}
