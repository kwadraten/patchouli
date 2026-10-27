namespace Patchouli.Extensions.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open Patchouli.Core.Ids
open Patchouli.Core.Results
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

[<Settings>]
type private EngineFactorySettings =
    { [<Field("Marker", Key = "marker"); Default("schema-default")>]
      Marker: string }

[<Collection(TestEnvironment.CollectionName)>]
type OcrEngineFactoryTests() =
    let definition () =
        OcrEngineDefinition("custom-engine", "Custom engine", "local_library", "custom-model",
            [| "page_image" |], ConfigurationVersion = 7)

    let snapshot (value: string) =
        ExtensionConfigSnapshot(Dictionary<string, string>(dict [ "marker", value ]), 7L)

    [<Fact>]
    member _.``engine metadata and settings type never invoke check or recognition`` () =
        let mutable checks = 0
        let mutable runs = 0
        let engine =
            Extension.createOcrEngine<EngineFactorySettings> (definition ())
                (fun _ _ _ _ -> checks <- checks + 1; Task.FromResult(Result.Success()))
                (fun _ _ _ _ -> runs <- runs + 1; Task.CompletedTask)
        Assert.Equal("custom-engine", engine.EngineDefinition.EngineId)
        Assert.Equal(7, engine.EngineDefinition.ConfigurationVersion)
        Assert.Equal(typeof<EngineFactorySettings>, (engine :?> IExtensionSettingsDefinition).SettingsType)
        Assert.Equal(0, checks)
        Assert.Equal(0, runs)

    [<Fact>]
    member _.``readiness and run observe their explicit pinned snapshot not another preset`` () = task {
        use resources = new FakeOcrResources()
        let first = snapshot "first-preset"
        let second = snapshot "second-preset"
        let seen = ResizeArray<string>()
        let engine =
            Extension.createOcrEngine<EngineFactorySettings> (definition ())
                (fun raw settings _ _ ->
                    Assert.Equal(7L, raw.Revision)
                    Assert.Equal(settings.Marker, raw.Values["marker"])
                    seen.Add("check:" + settings.Marker)
                    Task.FromResult(Result.Success()))
                (fun settings job _ _ ->
                    Assert.Equal(settings.Marker, job.Info.Config.Values["marker"])
                    seen.Add("run:" + settings.Marker)
                    Task.CompletedTask)
        let! firstReady = engine.CheckReadinessAsync(first, resources, CancellationToken.None)
        Assert.True(firstReady.IsSuccess)
        let! secondReady = engine.CheckReadinessAsync(second, resources, CancellationToken.None)
        Assert.True(secondReady.IsSuccess)
        let job = FakeOcrJob(OcrJobInfo("factory-job", DocumentInstanceId.New(), OcrJobOptions(), first), [||])
        do! engine.RunAsync(job, resources, CancellationToken.None)
        Assert.Equal<string array>([| "check:first-preset"; "check:second-preset"; "run:first-preset" |], seen.ToArray())
    }

    [<Fact>]
    member _.``failed readiness returns the engine diagnostic without executing job`` () = task {
        use resources = new FakeOcrResources()
        let mutable runs = 0
        let engine =
            Extension.createOcrEngine<EngineFactorySettings> (definition ())
                (fun _ _ _ _ -> Task.FromResult(Result.Failure("resource_missing", "Select model assets.")))
                (fun _ _ _ _ -> runs <- runs + 1; Task.CompletedTask)
        let! ready = engine.CheckReadinessAsync(snapshot "configured", resources, CancellationToken.None)
        Assert.False(ready.IsSuccess)
        Assert.Equal("resource_missing", ready.ErrorCode)
        Assert.Equal(0, runs)
    }
