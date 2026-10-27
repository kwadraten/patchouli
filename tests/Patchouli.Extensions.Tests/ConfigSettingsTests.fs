namespace Patchouli.Extensions.Tests

open System

open System.Collections.Generic
open System.Reflection
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Xunit

type private CommonSettings =
    { [<Field("Batch size", Key = "batch-size"); Default(8)>]
      BatchSize: int
      [<Field("Enabled"); Default(true)>]
      Enabled: bool }

[<Settings>]
type private SampleSettings =
    { [<Override("batch-size", 2)>]
      Common: CommonSettings
      [<Model("Translation model", FollowDefault = "translation")>]
      Model: ExtensionModelSelection option
      [<Field("Prompt"); Default("default prompt")>]
      Prompt: string }

type private MissingFieldSettings =
    { [<Field("Name")>]
      Name: string
      Count: int }

type private InvalidDefaultSettings =
    { [<Field("Prompt"); Default(4)>]
      Prompt: string }

type private NonRecordSettings() =
    member _.Prompt = "prompt"

[<Settings>]
type private OcrDefaultModelSettings =
    { [<Model("OCR model", Key = "model", FollowDefault = "ocr")>]
      Model: ExtensionModelSelection option }

[<Settings>]
type private InvalidModelDomainSettings =
    { [<Model("Model", FollowDefault = "not-a-domain")>]
      Model: ExtensionModelSelection option }

[<Collection(TestEnvironment.CollectionName)>]
type ConfigSettingsTests() =
    let snapshot values = ExtensionConfigSnapshot(Dictionary<string, string>(dict values), 0L)

    [<Fact>]
    member _.``unit settings use their canonical value without a runtime constructor`` () =
        let value = ConfigSettings.Read<unit>(snapshot [])
        Assert.Equal((), value)
        Assert.Empty(ConfigSettings.Describe<unit>())

    [<Fact>]
    member _.``OCR model schema has its own default domain without materializing translation defaults`` () =
        let field = Assert.Single(ConfigSettings.Describe<OcrDefaultModelSettings>())
        Assert.Equal("ocr", field.DefaultDomain)
        Assert.True(field.FollowDefault)
        Assert.Equal(None, ConfigSettings.Read<OcrDefaultModelSettings>(snapshot []).Model)

    [<Fact>]
    member _.``unknown default model domains are rejected`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Describe<InvalidModelDomainSettings>() |> ignore) |> ignore

    [<Fact>]
    member _.``attributed FSharp settings record reads defaults and flattened keys`` () =
        let settings = ConfigSettings.Read<SampleSettings>(snapshot [])
        Assert.Equal(2, settings.Common.BatchSize)
        Assert.True(settings.Common.Enabled)
        Assert.Equal("default prompt", settings.Prompt)

        let configured = ConfigSettings.Read<SampleSettings>(snapshot [ "batch-size", "12"; "enabled", "false" ])
        Assert.Equal(12, configured.Common.BatchSize)
        Assert.False(configured.Common.Enabled)

    [<Fact>]
    member _.``model setting reads a configured model selection`` () =
        let configured = ConfigSettings.Read<SampleSettings>(snapshot [
            "model", """{"providerId":"openai","model":"gpt-test"}"""
        ])
        Assert.Equal(Some(ExtensionModelSelection("openai", "gpt-test")), configured.Model)

    [<Fact>]
    member _.``snapshot values override profile defaults and override metadata is retained`` () =
        let configured = ConfigSettings.Read<SampleSettings>(snapshot [ "prompt", "snapshot prompt" ])
        Assert.Equal("snapshot prompt", configured.Prompt)
        match typeof<SampleSettings>.GetProperty("Common", BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) with
        | null -> Assert.Fail("Common settings property was not found")
        | property ->
            match property.GetCustomAttribute<OverrideAttribute>() with
            | null -> Assert.Fail("Common settings override attribute was not found")
            | overrideAttribute ->
                 Assert.Equal("batch-size", overrideAttribute.Key)
                 Assert.Equal(2, unbox<int> overrideAttribute.Value)

    [<Fact>]
    member _.``unmarked scalar record field is rejected`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Read<MissingFieldSettings>(snapshot []) |> ignore)
        |> ignore

    [<Fact>]
    member _.``default constant must match the attributed property type`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Read<InvalidDefaultSettings>(snapshot []) |> ignore)
        |> ignore

    [<Fact>]
    member _.``settings must be an FSharp record`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Read<NonRecordSettings>(snapshot []) |> ignore)
        |> ignore
