using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Mcp;
using Patchouli.Host.Translation;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class TranslationRunIdentityTests
{
    [Fact]
    public async Task Concurrent_replays_across_transport_adapters_start_once_and_keep_original_receipt()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        int settingsReads = 0;
        await using TranslationRunService service = new(new PageService(context.ConnectionFactory, context.Clock),
            context.Trees, context.Translations, () =>
            {
                Interlocked.Increment(ref settingsReads);
                return LlmAppSettings.Default();
            },
            TranslationTestExtensionHarness.CreateRunner((_, _, _) => Task.CompletedTask, true, null),
            _ => Task.CompletedTask);
        McpHostTranslationRunsApi first = new(service);
        McpHostTranslationRunsApi second = new(service);
        McpTranslationRunStartRequest request = new(context.DocumentId, "1", "zh-CN", RequestId: "stable");
        Result<McpTranslationRunStatusProjection>[] results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(index => (index % 2 == 0 ? first : second).StartAsync(request)));
        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.TaskId).Distinct().Should().ContainSingle();
        results.Count(result => !result.Value.Reused).Should().Be(1);
        settingsReads.Should().Be(1);
        Result<McpTranslationRunStatusProjection> replay = await first.StartAsync(request with { Pages = "1,1" });
        replay.Value.Reused.Should().BeTrue();
        replay.Value.TaskId.Should().Be(results[0].Value.TaskId);
        Result<McpTranslationRunStatusProjection> conflict =
            await second.StartAsync(request with { TargetLanguage = "en" });
        conflict.IsFailure.Should().BeTrue();
        (await service.ListAsync()).Value.Should().ContainSingle();
        Result<McpTranslationRunStatusProjection> fresh =
            await second.StartAsync(request with { RequestId = "new-run" });
        fresh.IsSuccess.Should().BeTrue();
        fresh.Value.TaskId.Should().NotBe(replay.Value.TaskId);
    }
}
