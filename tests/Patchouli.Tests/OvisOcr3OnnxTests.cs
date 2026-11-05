using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines.OvisOcr3;
using Xunit;

namespace Patchouli.Tests;

public sealed class OvisOcr3OnnxTests
{
    [Fact]
    public void Definition_is_offline_and_requires_local_model_files()
    {
        OcrEngineDefinition definition = OvisOcr3OnnxSession.GetDefinition();
        definition.EngineId.Should().Be("ovisocr3");
        definition.AdapterKind.Should().Be("local_library");
        definition.RequiresModelPath.Should().BeTrue();
        definition.ModelResources.Should().Contain(resource =>
            resource.Path == "ovisocr3_int4_single.onnx" &&
            resource.Sha256 == "7bafbef06e85847963d46fe520ba9767d18c7d2abdad91a25ce2f036558bcb3e");

        OvisOcr3OnnxSession.CheckReadiness(Config("")).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Structured_boxes_are_mapped_inside_the_issued_region()
    {
        OcrJobInput input = Input(new NormalizedBBox(0.25, 0.4, 0.5, 0.2));
        OcrExtensionBoxCandidate[] boxes = OvisOcr3OnnxSession.Parse(
            """
            [{"bbox":[100,200,900,800],"category":"text","content":"Alpha"},
             {"bbox":[0,0,1000,1000],"category":"table","content":"<table><tr><td>A</td></tr></table>"}]
            """, input);

        boxes.Should().HaveCount(2);
        boxes[0].InputId.Should().Be(input.Id);
        boxes[0].Candidate.BoxType.Should().Be(DocumentBoxType.Text);
        boxes[0].Candidate.Payload.Should().BeOfType<TextBoxPayload>()
            .Which.Markdown.Should().Be("Alpha");
        boxes[0].BBox.X.Should().BeApproximately(0.3, 1e-9);
        boxes[0].BBox.Y.Should().BeApproximately(0.44, 1e-9);
        boxes[0].BBox.Width.Should().BeApproximately(0.4, 1e-9);
        boxes[0].BBox.Height.Should().BeApproximately(0.12, 1e-9);
        boxes[1].Candidate.BoxType.Should().Be(DocumentBoxType.Table);
        boxes[1].BBox.Should().Be(input.Region);
    }

    [Theory]
    [InlineData(
        """[{"bbox":[0,0,1000,1000],"category":"text","content":"ok"},{"bbox":[0,0,1200,1000],"category":"text","content":"bad"}]""")]
    [InlineData(
        """[{"bbox":[0,0,1000,1000],"category":"text","content":"ok"},{"bbox":[0,0,1000,1000],"category":"text"}]""")]
    [InlineData("""[{"bbox":[0,0,1000,1000],"category":"unknown","content":"bad"}]""")]
    [InlineData("""{"boxes":[]}""")]
    public void Invalid_responses_are_rejected_without_partial_adoption(string response)
    {
        Action parse = () => OvisOcr3OnnxSession.Parse(response, Input(new NormalizedBBox(0, 0, 1, 1)));
        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Opt_in_live_model_recognizes_a_real_image_offline()
    {
        string? directory = Environment.GetEnvironmentVariable("PATCHOULI_OVISOCR3_TEST_MODEL_PATH");
        string? samplePath = Environment.GetEnvironmentVariable("PATCHOULI_OVISOCR3_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(samplePath))
        {
            return;
        }

        using OvisOcr3OnnxSession session = new(Config(directory));
        OcrJobInput input = Input(new NormalizedBBox(0, 0, 1, 1));
        using OcrInputImage image = new(input.Id, input.ContentType, File.ReadAllBytes(samplePath));
        OcrExtensionBoxCandidate[] boxes = session.Recognize(input, image, CancellationToken.None);

        boxes.Should().NotBeEmpty();
        boxes.Select(box => box.Candidate.Payload).OfType<TextBoxPayload>()
            .Select(payload => payload.Markdown).Should().Contain(markdown => markdown.Contains("HELLO OCR 123"));
    }

    private static OcrJobInput Input(NormalizedBBox region)
    {
        return new OcrJobInput(new OcrInputId("ovis-test"), region, "image/png");
    }

    private static ExtensionConfigSnapshot Config(string modelPath)
    {
        return new ExtensionConfigSnapshot(new Dictionary<string, string>
        {
            ["model-path"] = modelPath,
            ["max-new-tokens"] = "512"
        }, 1);
    }
}
