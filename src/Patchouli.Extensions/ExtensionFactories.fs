namespace Patchouli.Extensions.Sdk

open System
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Core.Ids
open Patchouli.Core.Bibliography.MetadataLookup
open Patchouli.Core.Results

type private TranslationFactoryDefinition<'Settings>(
    run: 'Settings -> ITranslationJob -> IExtensionResources -> CancellationToken -> Task) =
    interface ITranslationExtension with
        member _.RunAsync(job, resources, cancellationToken) =
            let settings = ConfigSettings.Read<'Settings>(job.Info.Config)
            run settings job resources cancellationToken

    interface IExtensionSettingsDefinition with
        member _.SettingsType = typeof<'Settings>

type private OcrFactoryDefinition<'Settings>(
    run: 'Settings -> IOcrJob -> IExtensionResources -> CancellationToken -> Task) =
    interface IOcrExtension with
        member _.RunAsync(job, resources, cancellationToken) =
            let settings = ConfigSettings.Read<'Settings>(job.Info.Config)
            run settings job resources cancellationToken

    interface IExtensionSettingsDefinition with
        member _.SettingsType = typeof<'Settings>

type private OcrEngineFactoryDefinition<'Settings>(
    definition: OcrEngineDefinition,
    check: ExtensionConfigSnapshot -> 'Settings -> IExtensionResources -> CancellationToken -> Task<Result>,
    run: 'Settings -> IOcrJob -> IExtensionResources -> CancellationToken -> Task) =
    interface IOcrExtension with
        member _.EngineDefinition = definition
        member _.CheckReadinessAsync(config, resources, cancellationToken) =
            // Readiness resolves the same frozen snapshot the run resolves: the raw pinned
            // config reaches the probe alongside the materialized settings, so a check can
            // never validate schema defaults while the run executes explicit legacy values.
            let settings = ConfigSettings.Read<'Settings>(config)
            check config settings resources cancellationToken
        member _.RunAsync(job, resources, cancellationToken) =
            let settings = ConfigSettings.Read<'Settings>(job.Info.Config)
            run settings job resources cancellationToken

    interface IExtensionSettingsDefinition with
        member _.SettingsType = typeof<'Settings>

type private MetadataFactoryDefinition<'Settings>(
    sourceDefinition: MetadataSourceDefinition,
    run: 'Settings -> NormalizedIdentifier -> IExtensionResources -> CancellationToken -> Task<Result<MetadataCandidate>>) =
    interface IMetadataExtension with
        member _.SourceDefinition = sourceDefinition
        member _.LookupAsync(identifier, config, resources, cancellationToken) =
            let settings = ConfigSettings.Read<'Settings>(config)
            run settings identifier resources cancellationToken

    interface IExtensionSettingsDefinition with
        member _.SettingsType = typeof<'Settings>

type private DerivedStateTranslationExtension(
    clear: CancellationToken -> Task,
    inner: ITranslationExtension) =
    interface ITranslationExtension with
        member _.Descriptor = inner.Descriptor
        member _.ConfigFields = inner.ConfigFields
        member _.RunAsync(job, resources, cancellationToken) = inner.RunAsync(job, resources, cancellationToken)

    interface IExtensionDerivedState with
        member _.ClearDerivedStateAsync(cancellationToken) = clear cancellationToken

    interface IExtensionSettingsDefinition with
        member _.SettingsType =
            match inner with
            | :? IExtensionSettingsDefinition as settings -> settings.SettingsType
            | _ -> invalidOp "The wrapped translation extension does not expose its settings type."

[<RequireQualifiedAccess>]
module Extension =
    /// Creates a translation implementation. Package identity and descriptor projection belong to the loader.
    let createTranslation<'Settings>
        (run: 'Settings -> ITranslationJob -> IExtensionResources -> CancellationToken -> Task) : ITranslationExtension =
        if isNull (box run) then nullArg "run"
        TranslationFactoryDefinition<'Settings>(run) :> ITranslationExtension

    /// Creates a metadata implementation. The host wrapper supplies package description and identity.
    let createMetadata<'Settings>
        (sourceDefinition: MetadataSourceDefinition)
        (run: 'Settings -> NormalizedIdentifier -> IExtensionResources -> CancellationToken -> Task<Result<MetadataCandidate>>) : IMetadataExtension =
        if isNull (box sourceDefinition) then nullArg "sourceDefinition"
        if isNull (box run) then nullArg "run"
        MetadataFactoryDefinition<'Settings>(sourceDefinition, run) :> IMetadataExtension

    /// Creates an OCR implementation with an extension-owned task-level driver. Package identity
    /// and descriptor projection belong to the loader.
    let createOcr<'Settings>
        (run: 'Settings -> IOcrJob -> IExtensionResources -> CancellationToken -> Task) : IOcrExtension =
        if isNull (box run) then nullArg "run"
        OcrFactoryDefinition<'Settings>(run) :> IOcrExtension

    /// Creates an OCR engine implementation that also carries its declarative
    /// <see cref="OcrEngineDefinition"/> and a non-executing readiness probe. The probe receives
    /// the raw pinned config alongside materialized settings — never a session — so readiness
    /// and run resolve the same effective values and discovery stays free of model sessions.
    let createOcrEngine<'Settings>
        (definition: OcrEngineDefinition)
        (check: ExtensionConfigSnapshot -> 'Settings -> IExtensionResources -> CancellationToken -> Task<Result>)
        (run: 'Settings -> IOcrJob -> IExtensionResources -> CancellationToken -> Task) : IOcrExtension =
        if isNull (box definition) then nullArg "definition"
        if isNull (box check) then nullArg "check"
        if isNull (box run) then nullArg "run"
        OcrEngineFactoryDefinition<'Settings>(definition, check, run) :> IOcrExtension

    /// Creates an OCR implementation for an engine that recognizes one input image at a time.
    /// The shared driver pulls pages lazily, materializes each input on demand, retries a failed
    /// input within a bounded attempt budget and settles every page exactly once.
    let createOcrPages<'Settings>
        (options: OcrPageDriverOptions)
        (recognize: 'Settings -> IExtensionResources -> OcrJobPage -> OcrJobInput -> OcrInputImage -> CancellationToken -> Task<OcrExtensionBoxCandidate array>) : IOcrExtension =
        if isNull (box options) then nullArg "options"
        if isNull (box recognize) then nullArg "recognize"
        createOcr<'Settings> (fun settings job resources cancellationToken ->
            let engine : OcrPageEngine =
                { Recognize = fun page input image token -> recognize settings resources page input image token }
            OcrJob.runPagesWithOptions options job engine cancellationToken)

    /// Adds extension-owned derived-state cleanup while preserving the translation and settings contracts.
    let withDerivedState
        (clear: CancellationToken -> Task)
        (extension: ITranslationExtension) : ITranslationExtension =
        if isNull (box clear) then nullArg "clear"
        if isNull (box extension) then nullArg "extension"
        if not (extension :? IExtensionSettingsDefinition) then
            invalidArg "extension" "A factory-created translation extension must expose its settings type."
        DerivedStateTranslationExtension(clear, extension) :> ITranslationExtension
