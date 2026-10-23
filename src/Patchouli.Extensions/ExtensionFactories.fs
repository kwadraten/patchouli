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

    /// Adds extension-owned derived-state cleanup while preserving the translation and settings contracts.
    let withDerivedState
        (clear: CancellationToken -> Task)
        (extension: ITranslationExtension) : ITranslationExtension =
        if isNull (box clear) then nullArg "clear"
        if isNull (box extension) then nullArg "extension"
        if not (extension :? IExtensionSettingsDefinition) then
            invalidArg "extension" "A factory-created translation extension must expose its settings type."
        DerivedStateTranslationExtension(clear, extension) :> ITranslationExtension
