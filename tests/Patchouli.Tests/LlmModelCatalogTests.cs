using System.Net;
using FluentAssertions;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class LlmModelCatalogTests
{
    [Fact]
    public async Task Local_catalog_remains_available_while_remote_refresh_is_pending()
    {
        LlmModelCatalog catalog = new();
        string directory = Directory.CreateTempSubdirectory("models-dev-").FullName;
        try
        {
            catalog.GetModelIds("openai").Should().Contain("gpt-4o-mini");
            const string cached =
                """{"openai":{"models":{"cached-model":{"limit":{"context":123456,"output":2345}}}}}""";
            string path = Path.Combine(directory, "models-dev.json");
            await File.WriteAllTextAsync(path, cached);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));

            RefreshHandler handler = new();
            using HttpClient http = new(handler);
            Task refresh = catalog.RefreshAsync(directory, http);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                refresh.IsCompleted.Should().BeFalse();
                catalog.GetModelIds("openai").Should().Equal("cached-model");
                catalog.Find("openai", "cached-model")!.ContextLimit.Should().Be(123456);
                catalog.Find("openai", "cached-model")!.MaxOutputTokens.Should().Be(2345);
            }
            finally
            {
                handler.Response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"openai":{"models":{"new-model":{"limit":{"context":240000}}}}}""")
                });
                await refresh;
            }

            catalog.GetModelIds("openai").Should().Equal("new-model");
            (await File.ReadAllTextAsync(path)).Should().Contain("new-model");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Model_limits_come_from_the_model_catalog()
    {
        LlmProviderRuntimeSettings settings = new(LlmProviderCatalog.Find("openai")!, "key",
            "https://api.openai.com/{0}/{1}", "gpt-4o-mini", "", "", "");
        LlmModelCatalog.Shared.Find("openai", settings.Model)!.ContextLimit.Should().Be(128000);
        settings.ContextWindowTokens.Should().Be(128000);
        (settings with { Model = "custom-unlisted-model" }).ContextWindowTokens.Should()
            .Be(LlmProviderAppSettings.DefaultContextWindowTokens);

        LlmTransportRequest request = new(settings, settings.Model,
            [new LlmTransportMessage(LlmChatRole.User, [new LlmMessagePart.LlmTextPart("hello")])],
            [], null, null, false, null, null);
        LlmModelsDevTransport.PrepareRequest(request).MaxTokens.Should().Be(16384);
        LlmModelsDevTransport.PrepareRequest(request with { MaxTokens = 256 }).MaxTokens.Should().Be(256);
    }

    private sealed class RefreshHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HttpResponseMessage> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.RequestUri.Should().Be(new Uri("https://models.dev/api.json"));
            Started.SetResult();
            return Response.Task.WaitAsync(cancellationToken);
        }
    }
}
