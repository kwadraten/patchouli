namespace Patchouli.Extensions.Tests

open System
open System.Collections.Generic
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Xunit

[<Settings>]
type private ManagedFileSettings =
    { [<Field("Model directory", Key = "model-path", Kind = ConfigValueKind.DirectoryPath); Default("");
        ManagedFile("model/det.onnx", "https://example.test/models/det.onnx",
            ExpectedBytes = 1024L, Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            Version = "v1", DirectoryKind = "test-models");
        ManagedFile("model/rec.onnx", "http://example.test/models/rec.onnx", ExpectedBytes = 2048L,
            DirectoryKind = "test-models")>]
      ModelPath: string
      [<Field("Layout model file", Key = "layout-path", Kind = ConfigValueKind.FilePath); Default("");
        ManagedFile("pp.onnx", "https://example.test/models/pp.onnx", ExpectedBytes = 4096L,
            DirectoryKind = "pp-models", WhenKey = "layout-model", WhenValue = "pp",
            RequiredWhenKey = "use-layout", RequiredWhenValue = "true",
            RequiredScopes = "document, pages,region");
        ManagedFile("yolo.onnx", "https://example.test/models/yolo.onnx",
            DirectoryKind = "yolo-models", WhenKey = "layout-model", WhenValue = "yolo",
            RequiredWhenKey = "use-layout", RequiredWhenValue = "true")>]
      LayoutPath: string
      [<Field("Layout model", Key = "layout-model", Kind = ConfigValueKind.Choice,
          Choices = [| "pp"; "yolo" |]); Default("pp")>]
      LayoutModel: string
      [<Field("Local layout", Key = "use-layout"); Default(true)>]
      UseLayout: bool }

[<Settings>]
type private TraversalManagedFileSettings =
    { [<Field("Model directory", Kind = ConfigValueKind.DirectoryPath); Default("");
        ManagedFile("../escape.onnx", "https://example.test/x")>]
      ModelPath: string }

[<Settings>]
type private RootedManagedFileSettings =
    { [<Field("Model directory", Kind = ConfigValueKind.DirectoryPath); Default("");
        ManagedFile("/abs/model.onnx", "https://example.test/x")>]
      ModelPath: string }

[<Settings>]
type private NonHttpManagedFileSettings =
    { [<Field("Model directory", Kind = ConfigValueKind.DirectoryPath); Default("");
        ManagedFile("model.onnx", "ftp://example.test/model.onnx")>]
      ModelPath: string }

[<Settings>]
type private BadShaManagedFileSettings =
    { [<Field("Model directory", Kind = ConfigValueKind.DirectoryPath); Default("");
        ManagedFile("model.onnx", "https://example.test/model.onnx", Sha256 = "not-a-sha")>]
      ModelPath: string }

[<Settings>]
type private HalfWhenManagedFileSettings =
    { [<Field("Model directory", Kind = ConfigValueKind.DirectoryPath); Default("");
        ManagedFile("model.onnx", "https://example.test/model.onnx", WhenKey = "use-cls")>]
      ModelPath: string }

[<Settings>]
type private TextKindManagedFileSettings =
    { [<Field("Model directory"); Default("");
        ManagedFile("model.onnx", "https://example.test/model.onnx")>]
      ModelPath: string }

[<Settings>]
type private MissingDefaultManagedFileSettings =
    { [<Field("Model directory", Kind = ConfigValueKind.DirectoryPath);
        ManagedFile("model.onnx", "https://example.test/model.onnx")>]
      ModelPath: string }

[<Settings>]
type private UnknownConditionKeySettings =
    { [<Field("Model directory", Kind = ConfigValueKind.DirectoryPath); Default("");
        ManagedFile("model.onnx", "https://example.test/model.onnx",
            RequiredWhenKey = "no-such-key", RequiredWhenValue = "true")>]
      ModelPath: string }

[<Collection(TestEnvironment.CollectionName)>]
type ManagedFileSchemaTests() =
    let snapshot values = ExtensionConfigSnapshot(Dictionary<string, string>(dict values), 0L)

    [<Fact>]
    member _.``managed file declarations project all contract metadata`` () =
        let fields = ConfigSettings.Describe<ManagedFileSettings>()
        Assert.Equal(4, fields.Count)

        let directory = Assert.Single(fields |> Seq.filter (fun f -> f.Key = "model-path"))
        Assert.Equal(ConfigValueKind.DirectoryPath, directory.Kind)
        Assert.Equal(2, directory.ManagedFiles.Count)
        let detector = directory.ManagedFiles[0]
        Assert.Equal("model/det.onnx", detector.RelativePath)
        Assert.Equal("https://example.test/models/det.onnx", detector.DownloadUrl)
        Assert.True(detector.ExpectedBytes.HasValue)
        Assert.Equal(1024L, detector.ExpectedBytes.Value)
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            detector.Sha256)
        Assert.Equal("v1", detector.Version)
        Assert.Equal("test-models", detector.DirectoryKind)
        Assert.Null(detector.WhenKey)
        Assert.Null(detector.RequiredScopes)
        let recognizer = directory.ManagedFiles[1]
        Assert.Null(recognizer.Sha256)

        let file = Assert.Single(fields |> Seq.filter (fun f -> f.Key = "layout-path"))
        Assert.Equal(ConfigValueKind.FilePath, file.Kind)
        Assert.Equal(2, file.ManagedFiles.Count)
        let pp = file.ManagedFiles[0]
        Assert.Equal("layout-model", pp.WhenKey)
        Assert.Equal("pp", pp.WhenValue)
        Assert.Equal("use-layout", pp.RequiredWhenKey)
        Assert.Equal("true", pp.RequiredWhenValue)
        match Option.ofObj pp.RequiredScopes with
        | Some scopes -> Assert.Equal<string>([| "document"; "pages"; "region" |], Seq.toArray scopes)
        | None -> Assert.Fail("RequiredScopes should parse")
        Assert.False(file.ManagedFiles[1].ExpectedBytes.HasValue)

    [<Fact>]
    member _.``path fields read stored values as plain strings`` () =
        let settings =
            ConfigSettings.Read<ManagedFileSettings>(snapshot
                [ "model-path", "D:\\models"; "layout-path", "/tmp/layout.onnx" ])
        Assert.Equal("D:\\models", settings.ModelPath)
        Assert.Equal("/tmp/layout.onnx", settings.LayoutPath)

    [<Fact>]
    member _.``parent traversal is rejected`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Describe<TraversalManagedFileSettings>() |> ignore) |> ignore

    [<Fact>]
    member _.``rooted paths are rejected`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Describe<RootedManagedFileSettings>() |> ignore) |> ignore

    [<Fact>]
    member _.``non-http download urls are rejected`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Describe<NonHttpManagedFileSettings>() |> ignore) |> ignore

    [<Fact>]
    member _.``malformed sha declarations are rejected`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Describe<BadShaManagedFileSettings>() |> ignore) |> ignore

    [<Fact>]
    member _.``half a selection condition pair is rejected`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Describe<HalfWhenManagedFileSettings>() |> ignore) |> ignore

    [<Fact>]
    member _.``managed files require the path field kinds`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Describe<TextKindManagedFileSettings>() |> ignore) |> ignore

    [<Fact>]
    member _.``managed files require an explicit default`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Describe<MissingDefaultManagedFileSettings>() |> ignore) |> ignore

    [<Fact>]
    member _.``condition keys must name declared configuration keys`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Describe<UnknownConditionKeySettings>() |> ignore) |> ignore
