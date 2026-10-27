using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Llm;

namespace Patchouli.Tests;

/// <summary>
/// Hosted extension LLM resource fixtures: the <see cref="HostExtensionResources"/> "ocr" domain
/// must bind the frozen OCR provider/model/reasoning slot, carry typed image bytes into the
/// provider-shaped transport request, and stay isolated from translation/chat defaults. A
/// recording transport stands in for the wire — these tests never touch the network or a real
/// credential store.
/// </summary>
public sealed class ExtensionVisionLlmTests
{
    // Real PNG file signature + padding: the host validates bytes against the declared media type.
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public async Task Ocr_bound_llm_sends_the_image_with_ocr_model_and_reasoning()
    {
        RecordingTransport transport = new();
        HostExtensionResources resources = CreateResources(OcrSettings(), transport);

        ILlmCapability llm = resources.ResolveLlm("ocr", null);
        LlmResponse response = await llm.CompleteAsync(new LlmRequest(
        [
            new LlmMessage("system", "You are a strict OCR engine."),
            new LlmMessage("user", "Recognize.")
            {
                Images = [new LlmImage(PngBytes, "image/png", "high")]
            }
        ]));

        response.Text.Should().Be("{}");
        transport.Requests.Should().ContainSingle();
        LlmTransportRequest sent = transport.Requests[0];
        sent.Provider.ProviderId.Should().Be("anthropic");
        sent.Model.Should().Be("claude-vision");
        sent.ReasoningEffort.Should().Be("high");
        sent.Messages.Last().Role.Should().Be(LlmChatRole.User);
        sent.Messages.Last().Parts.OfType<LlmMessagePart.LlmTextPart>()
            .Should().Contain(text => text.Text == "Recognize.");
        LlmMessagePart.LlmImagePart image = sent.Messages.Last().Parts
            .OfType<LlmMessagePart.LlmImagePart>().Should().ContainSingle().Subject;
        image.MimeType.Should().Be("image/png");
        image.Detail.Should().Be("high");
        image.DataUriOrUrl.Should().StartWith("data:image/png;base64,");
        Convert.FromBase64String(image.DataUriOrUrl["data:image/png;base64,".Length..])
            .Should().Equal(PngBytes);
    }

    [Fact]
    public async Task Ocr_bound_llm_never_inherits_translation_or_chat_defaults()
    {
        RecordingTransport transport = new();
        HostExtensionResources resources = CreateResources(OcrSettings(), transport);

        await resources.ResolveLlm("ocr", null).CompleteAsync(new LlmRequest(
            [new LlmMessage("user", "Recognize.") { Images = [new LlmImage(PngBytes, "image/png")] }]));
        await resources.Llm.CompleteAsync(new LlmRequest([new LlmMessage("user", "Translate.")]));

        transport.Requests.Should().HaveCount(2);
        transport.Requests[0].Provider.ProviderId.Should().Be("anthropic");
        transport.Requests[0].Model.Should().Be("claude-vision");
        transport.Requests[0].ReasoningEffort.Should().Be("high");
        transport.Requests[0].Messages.SelectMany(message => message.Parts)
            .Should().Contain(part => part is LlmMessagePart.LlmImagePart);
        // The unbound capability keeps the translation slot and carries no OCR reasoning.
        transport.Requests[1].Provider.ProviderId.Should().Be("openai");
        transport.Requests[1].Model.Should().Be("gpt-4o-mini");
        transport.Requests[1].ReasoningEffort.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Explicit_domain_on_an_unbound_request_selects_the_ocr_slot()
    {
        RecordingTransport transport = new();
        HostExtensionResources resources = CreateResources(OcrSettings(), transport);

        await resources.Llm.CompleteAsync(new LlmRequest(
            [new LlmMessage("user", "Recognize.") { Images = [new LlmImage(PngBytes, "image/png")] }])
        {
            Domain = "ocr",
            ReasoningEffort = "low"
        });

        LlmTransportRequest sent = transport.Requests.Should().ContainSingle().Subject;
        sent.Provider.ProviderId.Should().Be("anthropic");
        sent.Model.Should().Be("claude-vision");
        // A per-request reasoning effort overrides the bound OCR default.
        sent.ReasoningEffort.Should().Be("low");
    }

    [Fact]
    public async Task Selection_override_retargets_the_ocr_bound_capability()
    {
        RecordingTransport transport = new();
        HostExtensionResources resources = CreateResources(OcrSettings(), transport);

        ILlmCapability llm = resources.ResolveLlm("ocr", new ExtensionModelSelection("openai", "gpt-4o"));
        await llm.CompleteAsync(new LlmRequest(
            [new LlmMessage("user", "Recognize.") { Images = [new LlmImage(PngBytes, "image/png")] }]));

        LlmTransportRequest sent = transport.Requests.Should().ContainSingle().Subject;
        sent.Provider.ProviderId.Should().Be("openai");
        sent.Model.Should().Be("gpt-4o");
        sent.ReasoningEffort.Should().Be("high");
    }

    [Fact]
    public async Task ForRun_freezes_the_ocr_slot_against_later_settings_edits()
    {
        RecordingTransport transport = new();
        SettingsSlot live = new() { Current = OcrSettings() };
        HostExtensionResources resources = CreateResources(() => live.Current, transport);
        IExtensionResources run = resources.ForRun(new ExtensionConfigSnapshot(
            new Dictionary<string, string> { ["mode"] = "direct" }, 1));
        live.Current = live.Current with
        {
            OcrProviderId = "openai",
            OcrModel = "gpt-4o-mini",
            OcrReasoningEffort = "none"
        };

        ILlmCapability llm = run.ResolveLlm("ocr", null);
        await llm.CompleteAsync(new LlmRequest(
            [new LlmMessage("user", "Recognize.") { Images = [new LlmImage(PngBytes, "image/png")] }]));

        LlmTransportRequest sent = transport.Requests.Should().ContainSingle().Subject;
        sent.Provider.ProviderId.Should().Be("anthropic");
        sent.Model.Should().Be("claude-vision");
        sent.ReasoningEffort.Should().Be("high");
    }

    [Fact]
    public async Task Ocr_calls_on_different_frozen_runs_keep_their_own_snapshot()
    {
        RecordingTransport transport = new();
        SettingsSlot live = new() { Current = OcrSettings() };
        HostExtensionResources resources = CreateResources(() => live.Current, transport);
        IExtensionResources first = resources.ForRun(new ExtensionConfigSnapshot(
            new Dictionary<string, string>(), 1));
        live.Current = live.Current with { OcrModel = "claude-other", OcrReasoningEffort = "low" };
        IExtensionResources second = resources.ForRun(new ExtensionConfigSnapshot(
            new Dictionary<string, string>(), 2));

        await first.ResolveLlm("ocr", null).CompleteAsync(
            new LlmRequest([new LlmMessage("user", "One.")]));
        await second.ResolveLlm("ocr", null).CompleteAsync(
            new LlmRequest([new LlmMessage("user", "Two.")]));

        transport.Requests.Should().HaveCount(2);
        transport.Requests[0].Model.Should().Be("claude-vision");
        transport.Requests[0].ReasoningEffort.Should().Be("high");
        transport.Requests[1].Model.Should().Be("claude-other");
        transport.Requests[1].ReasoningEffort.Should().Be("low");
    }

    [Fact]
    public async Task Unknown_domain_and_bad_images_fail_without_calling_the_provider()
    {
        RecordingTransport transport = new();
        HostExtensionResources resources = CreateResources(OcrSettings(), transport);

        Func<Task> unknownDomain = () => Task.FromResult(resources.ResolveLlm("agents", null));
        await unknownDomain.Should().ThrowAsync<ArgumentException>();

        Func<Task> badMediaType = () => resources.ResolveLlm("ocr", null).CompleteAsync(new LlmRequest(
            [new LlmMessage("user", "x") { Images = [new LlmImage(PngBytes, "image/tiff")] }]));
        await badMediaType.Should().ThrowAsync<InvalidOperationException>();

        Func<Task> emptyBytes = () => resources.ResolveLlm("ocr", null).CompleteAsync(new LlmRequest(
            [new LlmMessage("user", "x") { Images = [new LlmImage([], "image/png")] }]));
        await emptyBytes.Should().ThrowAsync<InvalidOperationException>();

        Func<Task> assistantImage = () => resources.ResolveLlm("ocr", null).CompleteAsync(new LlmRequest(
            [new LlmMessage("assistant", "x") { Images = [new LlmImage(PngBytes, "image/png")] }]));
        await assistantImage.Should().ThrowAsync<InvalidOperationException>();

        // JPEG bytes mislabeled as PNG must be rejected by the signature check.
        Func<Task> mislabeled = () => resources.ResolveLlm("ocr", null).CompleteAsync(new LlmRequest(
        [
            new LlmMessage("user", "x")
            {
                Images = [new LlmImage([0xFF, 0xD8, 0xFF, 0xE0, 1, 2], "image/png")]
            }
        ]));
        await mislabeled.Should().ThrowAsync<InvalidOperationException>();

        Func<Task> badDetail = () => resources.ResolveLlm("ocr", null).CompleteAsync(new LlmRequest(
            [new LlmMessage("user", "x") { Images = [new LlmImage(PngBytes, "image/png", "ultra")] }]));
        await badDetail.Should().ThrowAsync<InvalidOperationException>();

        transport.Requests.Should().BeEmpty("invalid requests must never reach the provider");
    }

    [Fact]
    public async Task Mutating_the_callers_image_bytes_mid_flight_cannot_change_the_request()
    {
        RecordingTransport transport = new();
        HostExtensionResources resources = CreateResources(OcrSettings(), transport);
        byte[] callerBytes = (byte[])PngBytes.Clone();

        Task<LlmResponse> pending = resources.ResolveLlm("ocr", null).CompleteAsync(new LlmRequest(
            [new LlmMessage("user", "Recognize.") { Images = [new LlmImage(callerBytes, "image/png")] }]));
        Array.Clear(callerBytes); // the snapshot was already taken synchronously at call entry
        LlmResponse response = await pending;

        response.Text.Should().Be("{}");
        LlmMessagePart.LlmImagePart image = transport.Requests.Should().ContainSingle().Subject
            .Messages.SelectMany(message => message.Parts)
            .OfType<LlmMessagePart.LlmImagePart>().Should().ContainSingle().Subject;
        Convert.FromBase64String(image.DataUriOrUrl["data:image/png;base64,".Length..])
            .Should().Equal(PngBytes, "the provider sees the bytes captured at call entry");
    }

    /// <summary>Mutable live-settings slot — the settings delegate must observe later edits.</summary>
    private sealed class SettingsSlot
    {
        public LlmAppSettings Current { get; set; } = LlmAppSettings.Default();
    }

    private static LlmAppSettings OcrSettings()
    {
        return LlmAppSettings.Default() with
        {
            OcrProviderId = "anthropic",
            OcrModel = "claude-vision",
            OcrReasoningEffort = "high",
            ChatProviderId = "openai",
            ChatModel = "gpt-4o-mini",
            Translation = new TranslationAppSettings { ProviderId = "openai", Model = "gpt-4o-mini" }
        };
    }

    private static HostExtensionResources CreateResources(LlmAppSettings settings,
        RecordingTransport transport)
    {
        return CreateResources(() => settings, transport);
    }

    private static HostExtensionResources CreateResources(Func<LlmAppSettings> settings,
        RecordingTransport transport)
    {
        return new HostExtensionResources(settings, new StubCredentialStore(),
            new HttpMessageInvoker(new ThrowingHandler()), _ => transport);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The extension HTTP channel is unused in this fixture.");
        }
    }

    private sealed class StubCredentialStore : ICredentialStore
    {
        public Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
            string secretValue, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<ProviderCredentialMetadata>.Failure(
                AppErrorCodes.UnsupportedOperation, "The fixture store is read-only."));
        }

        public Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<string>.Success("sk-fixture"));
        }

        public Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "The fixture store is read-only."));
        }

        public Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<ProviderCredentialMetadata>>.Success([]));
        }
    }

    private sealed class RecordingTransport : ILlmChatTransport
    {
        public List<LlmTransportRequest> Requests { get; } = [];

        public Task<LlmTransportResult> SendAsync(LlmTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(LlmTransportResult.Success("{}", request.Model, "stop",
                LlmUsage.Empty));
        }
    }
}
