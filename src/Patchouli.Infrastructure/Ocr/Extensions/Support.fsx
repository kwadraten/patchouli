namespace BuiltInOcr

open System.Threading
open System.Threading.Tasks
open Patchouli.Core.Results
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Patchouli.Ocr.Engines

/// Shared plumbing for the compiled native ONNX OCR packages (RapidOCR, NDLOCR-Lite,
/// NDLKotenOCR-Lite). Everything routes through Patchouli.Ocr.Engines: declarative metadata and
/// the non-executing readiness probe never construct a model session, and each run binds exactly
/// one session to the job's frozen configuration for the duration of the shared page driver.
module Support =

    /// The engine's declarative metadata straight from the support assembly — discovery and
    /// capability listing never construct an ONNX session.
    let definition (engineId: string) : OcrEngineDefinition =
        NativeOcrSession.GetDefinition(engineId)

    /// Non-executing readiness probe over the SAME pinned snapshot the run resolves. The raw
    /// config is used verbatim — never a snapshot rebuilt from materialized settings — so an
    /// explicit legacy parameters-json value is never shadowed by a package schema default.
    /// The probe resolves configuration and checks model manifests only; it opens no session.
    let check (engineId: string) (config: ExtensionConfigSnapshot) (_settings: 'Settings)
              (_resources: IExtensionResources) (_cancellationToken: CancellationToken)
              : Task<Result> =
        Task.FromResult(NativeOcrSession.CheckReadiness(engineId, config))

    /// The task-level run: one native session bound to the job's immutable configuration feeds
    /// the shared page driver, which pulls pages lazily, bounds per-input retries, and settles
    /// every page exactly once. The session is disposed when the driver's calls have exited.
    /// Model weights stay in the configured model directory and are never copied.
    let run (engineId: string) (_settings: 'Settings) (job: IOcrJob)
            (_resources: IExtensionResources) (cancellationToken: CancellationToken) : Task =
        task {
            use session = new NativeOcrSession(engineId, job.Info.Config)
            let engine: OcrPageEngine =
                { Recognize = fun page input image token ->
                    Task.FromResult(session.Recognize(page, input, image, token)) }
            do! OcrJob.runPages job engine cancellationToken
        } :> Task
