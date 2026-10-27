using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Ocr;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// The real built-in FSX declarations: managed-file metadata must survive static schema
/// extraction and the generated-schema JSON path, and an arbitrary new package declares files
/// through the same generic mechanism — no engine switch anywhere.
/// </summary>
[Trait("Category", "fcs")]
public sealed class ExtensionManagedFileSchemaTests
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static async Task<IReadOnlyList<ConfigFieldDescriptor>> CheckFieldsAsync(
        ExtensionPackageSnapshot package)
    {
        ExtensionCheckResult result = await new ExtensionCompiler().CheckAsync(package,
            CancellationToken.None);
        result.Succeeded.Should().BeTrue("{0}", string.Join(Environment.NewLine,
            result.Diagnostics.Select(d =>
                $"{d.Location.FileName}({d.Location.Line},{d.Location.Column}): {d.Code}: {d.Message}")));
        return result.ConfigFields;
    }

    private static IReadOnlyList<ExtensionManagedFile> ManagedFilesOf(
        IReadOnlyList<ConfigFieldDescriptor> fields, string key)
    {
        return fields.Single(field => field.Key == key).ManagedFiles;
    }

    [Fact]
    public void Managed_files_survive_the_generated_schema_json_roundtrip()
    {
        ConfigFieldDescriptor descriptor = new("model-path", "Model", ConfigValueKind.DirectoryPath,
            false, "hint", "")
        {
            ManagedFiles =
            [
                new ExtensionManagedFile("m/det.onnx", "https://example.test/det.onnx", 12,
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "v1",
                    "rapidocr-models", "use-cls", "true", "rec", "1", ["document"])
            ]
        };
        string json = JsonSerializer.Serialize(descriptor, SchemaJson);
        ConfigFieldDescriptor? roundtripped =
            JsonSerializer.Deserialize<ConfigFieldDescriptor>(json, SchemaJson);

        roundtripped.Should().NotBeNull();
        roundtripped!.Kind.Should().Be(ConfigValueKind.DirectoryPath);
        ExtensionManagedFile file = roundtripped.ManagedFiles.Should().ContainSingle().Subject;
        file.RelativePath.Should().Be("m/det.onnx");
        file.DownloadUrl.Should().Be("https://example.test/det.onnx");
        file.ExpectedBytes.Should().Be(12);
        file.Sha256.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        file.Version.Should().Be("v1");
        file.DirectoryKind.Should().Be("rapidocr-models");
        file.WhenKey.Should().Be("use-cls");
        file.WhenValue.Should().Be("true");
        file.RequiredWhenKey.Should().Be("rec");
        file.RequiredWhenValue.Should().Be("1");
        file.RequiredScopes.Should().Equal("document");
    }

    [Fact]
    public async Task RapidOcr_declares_all_three_weights_with_a_conditional_classifier()
    {
        IReadOnlyList<ConfigFieldDescriptor> fields = await CheckFieldsAsync(
            BuiltInOcrExtensions.Packages.Single(p => p.Id == "ocr.rapidocr"));

        ConfigFieldDescriptor path = fields.Single(f => f.Key == "model-path");
        path.Kind.Should().Be(ConfigValueKind.DirectoryPath);
        IReadOnlyList<ExtensionManagedFile> files = path.ManagedFiles;
        files.Should().HaveCount(3);
        files.Should().AllSatisfy(file =>
        {
            file.DownloadUrl.Should().StartWith("https://www.modelscope.cn/");
            file.DirectoryKind.Should().Be("rapidocr-models");
            file.ExpectedBytes.Should().BeGreaterThan(0);
            file.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        });

        files.Select(file => file.RelativePath).Should()
            .BeEquivalentTo("PP-OCRv6_det_small.onnx", "PP-OCRv6_rec_small.onnx",
                "ch_ppocr_mobile_v2.0_cls_mobile.onnx");
        ExtensionManagedFile classifier =
            files.Single(file => file.RelativePath.Contains("cls", StringComparison.Ordinal));
        classifier.RequiredWhenKey.Should().Be("use-cls",
            "the classifier is only required when use-cls is enabled");
        classifier.RequiredWhenValue.Should().Be("true");
        files.Where(file => !file.RelativePath.Contains("cls", StringComparison.Ordinal))
            .Should().AllSatisfy(file => file.RequiredWhenKey.Should().BeNull());
    }

    [Fact]
    public async Task NdlOcrLite_declares_the_pinned_commit_manifest_with_hashes()
    {
        IReadOnlyList<ConfigFieldDescriptor> fields = await CheckFieldsAsync(
            BuiltInOcrExtensions.Packages.Single(p => p.Id == "ocr.ndlocr-lite"));

        IReadOnlyList<ExtensionManagedFile> files = ManagedFilesOf(fields, "model-path");
        files.Should().HaveCount(6);
        files.Should().AllSatisfy(file =>
        {
            file.DownloadUrl.Should().Contain(
                "ndlocr-lite/6feccb29e33c2467ea48894e66502a7788ae2b2d/",
                "the manifest is pinned to the reviewed upstream commit");
            file.DirectoryKind.Should().Be("ndlocr-lite-models");
            file.Sha256.Should().MatchRegex("^[0-9a-f]{64}$",
                "ndlocr-lite ships verified hashes for every weight");
            file.ExpectedBytes.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public async Task NdlKoten_declares_its_manifest_with_honest_optional_hashes()
    {
        IReadOnlyList<ConfigFieldDescriptor> fields = await CheckFieldsAsync(
            BuiltInOcrExtensions.Packages.Single(p => p.Id == "ocr.ndlkotenocr-lite"));

        IReadOnlyList<ExtensionManagedFile> files = ManagedFilesOf(fields, "model-path");
        files.Should().HaveCount(4);
        files.Should().AllSatisfy(file =>
        {
            file.DownloadUrl.Should().Contain("ndlkotenocr-lite");
            file.DirectoryKind.Should().Be("ndl-koten-models");
            file.Sha256.Should().BeNull(
                "upstream publishes no verified hashes — the field stays honestly optional");
            file.ExpectedBytes.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public async Task Multimodal_declares_both_layout_models_behind_mode_and_scope_gates()
    {
        IReadOnlyList<ConfigFieldDescriptor> fields = await CheckFieldsAsync(
            BuiltInOcrExtensions.Packages.Single(p => p.Id == "ocr.multimodal"));

        ConfigFieldDescriptor path = fields.Single(f => f.Key == "model-path");
        path.Kind.Should().Be(ConfigValueKind.FilePath,
            "the vision layout path names a concrete file, not a directory");
        IReadOnlyList<ExtensionManagedFile> files = path.ManagedFiles;
        files.Should().HaveCount(2);

        files.Should().AllSatisfy(file =>
        {
            file.WhenKey.Should().Be("layout-model",
                "each weight only applies to its selected layout model");
            file.RequiredWhenKey.Should().Be("use-local-layout");
            file.RequiredWhenValue.Should().Be("true");
            file.RequiredScopes.Should().BeEquivalentTo(new[] { "document", "pages" },
                "every full-page input feeds the detector; only an explicit region bypasses it");
        });
        files.Single(file => file.WhenValue == "doclayout-yolo").DownloadUrl
            .Should().Contain("DocLayout-YOLO");
        files.Single(file => file.WhenValue == "pp-doclayout-v3").DownloadUrl
            .Should().Contain("PP-DocLayoutV3");
    }

    [Fact]
    public async Task Visual_guided_pdf_declares_model_id_conditioned_layout_files()
    {
        IReadOnlyList<ConfigFieldDescriptor> fields = await CheckFieldsAsync(
            BuiltInPdfTextLayerExtensions.Packages.Single(p => p.Id == "pdf-text.visual-guided"));

        ConfigFieldDescriptor path = fields.Single(f => f.Key == "model-path");
        path.Kind.Should().Be(ConfigValueKind.FilePath);
        IReadOnlyList<ExtensionManagedFile> files = path.ManagedFiles;
        files.Should().HaveCount(2);
        files.Should().AllSatisfy(file => file.WhenKey.Should().Be("model-id"));
        files.Select(file => file.WhenValue).Should()
            .BeEquivalentTo("pp-doclayout-v3", "doclayout-yolo");
        files.Should().AllSatisfy(file => file.RequiredScopes.Should().BeNull(
            "layout detection is the provider's core mechanism — required in every scope"));
    }

    [Fact]
    public async Task Native_text_layer_providers_declare_no_managed_files()
    {
        foreach (string id in new[] { "pdf-text.native-xycut", "pdf-text.native-gaptree" })
        {
            IReadOnlyList<ConfigFieldDescriptor> fields = await CheckFieldsAsync(
                BuiltInPdfTextLayerExtensions.Packages.Single(p => p.Id == id));
            fields.Should().AllSatisfy(field => field.ManagedFiles.Should().BeEmpty(
                $"{id} is a pure text-layer provider — no image/model assets may be declared"));
        }
    }

    [Fact]
    public async Task An_arbitrary_new_package_declares_files_without_any_engine_switch()
    {
        ExtensionPackageSnapshot package = new("test.managed-files", new Dictionary<string, string>
        {
            ["main.fsx"] = """
                           open System.Threading.Tasks
                           open Patchouli.Extensions.Contracts
                           open Patchouli.Extensions.Sdk

                           [<Settings>]
                           type Settings = {
                               [<Field("Data directory", Key = "data-path",
                                   Kind = ConfigValueKind.DirectoryPath)>]
                               [<Default("")>]
                               [<ManagedFile("vocab.json", "https://example.test/vocab.json",
                                   ExpectedBytes = 32L, DirectoryKind = "vocab")>]
                               DataPath: string
                           }

                           /// <summary>Managed-file test extension.</summary>
                           /// <patchouli-extension id="test.managed-files" kind="translation" version="1.0.0" host-api="patchouli.extensions/3">
                           /// <name>Managed files</name>
                           /// </patchouli-extension>
                           let extension =
                               Extension.createTranslation<unit>
                                   (fun () _ _ _ -> Task.CompletedTask)
                           """
        });

        IReadOnlyList<ConfigFieldDescriptor> fields = await CheckFieldsAsync(package);
        ConfigFieldDescriptor path = fields.Single(f => f.Key == "data-path");
        path.Kind.Should().Be(ConfigValueKind.DirectoryPath);
        ExtensionManagedFile file = path.ManagedFiles.Should().ContainSingle().Subject;
        file.RelativePath.Should().Be("vocab.json");
        file.DirectoryKind.Should().Be("vocab");
        file.ExpectedBytes.Should().Be(32);
    }
}
