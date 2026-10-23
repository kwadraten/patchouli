namespace Patchouli.Extensions.Tests.Configuration

open Patchouli.Extensions.Tests

open System
open System.Collections.Generic
open System.Reflection
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Xunit

type private TextSettings =
    { [<Field("Greeting", Key = "greeting"); Default("hello")>]
      Greeting: string }

type private PlanCacheSettings =
    { [<Field("Value", Key = "value"); Default("default")>]
      Value: string }

type private ValueSettings =
    { [<Field("Count", Key = "count"); Default(5); Range(1, 9)>]
      Count: int
      [<Field("Enabled", Key = "enabled"); Default(false)>]
      Enabled: bool
      [<Field("Scale", Key = "scale"); Default("1.25"); Range("0.5", "2.5")>]
      Scale: decimal
      [<Field("Mode", Key = "mode", Kind = ConfigValueKind.Choice, Choices = [| "x"; "y" |]); Default("x")>]
      Mode: string }

type private RequiredSettings =
    { [<Field("Credential", Key = "credential", Kind = ConfigValueKind.CredentialReference, Required = true)>]
      Credential: string }

type private ModelSettings =
    { [<Model("Required model", Key = "required-model")>]
      RequiredModel: ExtensionModelSelection
      [<Model("Optional model", Key = "optional-model", FollowDefault = "translation")>]
      OptionalModel: ExtensionModelSelection option }

type private OptionalModelSettings =
    { [<Model("Optional model", Key = "optional-model", FollowDefault = "translation")>]
      Model: ExtensionModelSelection option }

type private CommonSettings =
    { [<Field("Parallel requests", Key = "concurrency"); Default(4); Range(1, 16)>]
      Concurrency: int }

type private NestedSettings =
    { [<Override("concurrency", 1)>]
      Common: CommonSettings }

type private MissingFieldSettings =
    { [<Field("Name")>]
      Name: string
      Count: int }

type private WrongDefaultSettings =
    { [<Field("Text"); Default(4)>]
      Text: string }

type private DuplicateKeySettings =
    { [<Field("First", Key = "same"); Default("a")>]
      First: string
      [<Field("Second", Key = "same"); Default("b")>]
      Second: string }

type private InvalidRangeSettings =
    { [<Field("Count"); Default(12); Range(1, 9)>]
      Count: int }

type private NonRecordSettings() =
    member _.Prompt = "prompt"

[<Collection(TestEnvironment.CollectionName)>]
type ConfigTests() =
    let snapshot (pairs: (string * string) list) =
        ExtensionConfigSnapshot(Dictionary<string, string>(dict pairs), 0L)

    [<Fact>]
    member _.``attribute records read defaults and stable persisted keys`` () =
        let settings = ConfigSettings.Read<TextSettings>(snapshot [])
        Assert.Equal("hello", settings.Greeting)
        let configured = ConfigSettings.Read<TextSettings>(snapshot [ "greeting", "welcome" ])
        Assert.Equal("welcome", configured.Greeting)

    [<Fact>]
    member _.``integer boolean decimal and choice fields parse stored values`` () =
        let values = ConfigSettings.Read<ValueSettings>(snapshot [
            "count", "7"
            "enabled", "true"
            "scale", "2.125"
            "mode", "y"
        ])
        Assert.Equal(7, values.Count)
        Assert.True(values.Enabled)
        Assert.Equal(2.125m, values.Scale)
        Assert.Equal("y", values.Mode)

    [<Fact>]
    member _.``typed fields reject invalid persisted values`` () =
        for key, value in [ "count", "bad"; "enabled", "yes"; "scale", "bad"; "mode", "unknown" ] do
            Assert.Throws<InvalidOperationException>(fun () ->
                ConfigSettings.Read<ValueSettings>(snapshot [ key, value ]) |> ignore)
            |> ignore

    [<Fact>]
    member _.``required credential fields reject missing and blank values`` () =
        for values in [ []; [ "credential", " " ] ] do
            Assert.Throws<InvalidOperationException>(fun () ->
                ConfigSettings.Read<RequiredSettings>(snapshot values) |> ignore)
            |> ignore
        Assert.Equal("secret-ref", (ConfigSettings.Read<RequiredSettings>(snapshot [ "credential", "secret-ref" ])).Credential)

    [<Fact>]
    member _.``required model selections require provider and model`` () =
        Assert.Throws<InvalidOperationException>(fun () ->
            ConfigSettings.Read<ModelSettings>(snapshot []) |> ignore)
        |> ignore
        for json in [ "{}"; """{"providerId":"openai"}"""; """{"model":"gpt-test"}""" ] do
            Assert.Throws<InvalidOperationException>(fun () ->
                ConfigSettings.Read<ModelSettings>(snapshot [ "required-model", json ]) |> ignore)
            |> ignore

    [<Fact>]
    member _.``optional model maps empty and blank selections to none`` () =
        for json in [ ""; "{}"; """{"providerId":"","model":""}""" ] do
            let settings = ConfigSettings.Read<OptionalModelSettings>(snapshot [ "optional-model", json ])
            Assert.True(settings.Model.IsNone)
        Assert.True((ConfigSettings.Read<OptionalModelSettings>(snapshot [])).Model.IsNone)

    [<Fact>]
    member _.``optional single model override preserves the other empty member`` () =
        let settings = ConfigSettings.Read<OptionalModelSettings>(snapshot [
            "optional-model", """{"providerId":"openai"}"""
        ])
        Assert.Equal(Some(ExtensionModelSelection("openai", "")), settings.Model)

    [<Fact>]
    member _.``Describe returns the validated fields through the runtime schema path`` () =
        let fields = ConfigSettings.Describe<ValueSettings>()
        Assert.Equal<string array>([| "count"; "enabled"; "scale"; "mode" |], fields |> Seq.map (fun field -> field.Key) |> Seq.toArray)
        Assert.Equal(ConfigValueKind.Choice, fields[3].Kind)
        let choices = fields[3].Choices |> Option.ofObj |> Option.defaultWith (fun () -> failwith "Missing choices")
        Assert.Equal<string array>([| "x"; "y" |], choices |> Seq.toArray)

    [<Fact>]
    member _.``nested CommonSettings flatten once and overrides its default`` () =
        let settings = ConfigSettings.Read<NestedSettings>(snapshot [])
        Assert.Equal(1, settings.Common.Concurrency)
        Assert.Equal(3, (ConfigSettings.Read<NestedSettings>(snapshot [ "concurrency", "3" ])).Common.Concurrency)

    [<Fact>]
    member _.``schema rejects unmarked fields duplicate keys and non-record types`` () =
        Assert.Throws<InvalidOperationException>(fun () -> ConfigSettings.Describe<MissingFieldSettings>() |> ignore) |> ignore
        Assert.Throws<InvalidOperationException>(fun () -> ConfigSettings.Describe<DuplicateKeySettings>() |> ignore) |> ignore
        Assert.Throws<InvalidOperationException>(fun () -> ConfigSettings.Describe<NonRecordSettings>() |> ignore) |> ignore

    [<Fact>]
    member _.``schema rejects defaults incompatible with their CLR member`` () =
        Assert.Throws<InvalidOperationException>(fun () -> ConfigSettings.Describe<WrongDefaultSettings>() |> ignore) |> ignore

    [<Fact>]
    member _.``range validation applies to defaults and saved values`` () =
        Assert.Throws<InvalidOperationException>(fun () -> ConfigSettings.Describe<InvalidRangeSettings>() |> ignore) |> ignore
        Assert.Throws<InvalidOperationException>(fun () -> ConfigSettings.Read<ValueSettings>(snapshot [ "count", "10" ]) |> ignore) |> ignore

    [<Fact>]
    member _.``Read guards a null snapshot`` () =
        Assert.Throws<ArgumentNullException>(fun () ->
            ConfigSettings.Read<TextSettings>(Unchecked.defaultof<ExtensionConfigSnapshot>) |> ignore)
        |> ignore

    [<Fact>]
    member _.``runtime read plan is built once per type and values remain snapshot local`` () =
        let counter =
            match typeof<ConfigSettings>.GetProperty("RuntimePlanBuildCount", BindingFlags.Static ||| BindingFlags.NonPublic) with
            | null -> failwith "Runtime plan build counter was not found."
            | property -> property
        let before = unbox<int> (counter.GetValue(null))
        let first = ConfigSettings.Read<PlanCacheSettings>(snapshot [ "value", "first" ])
        let second = ConfigSettings.Read<PlanCacheSettings>(snapshot [ "value", "second" ])
        let after = unbox<int> (counter.GetValue(null))
        Assert.Equal(before + 1, after)
        Assert.Equal("first", first.Value)
        Assert.Equal("second", second.Value)
