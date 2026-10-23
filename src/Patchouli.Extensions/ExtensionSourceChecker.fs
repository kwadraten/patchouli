namespace Patchouli.Extensions

open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts

/// Source-checking boundary implemented by the compiler sidecar process.
type IExtensionSourceChecker =
    abstract CheckAsync: ExtensionPackageSnapshot * CancellationToken -> Task<ExtensionCheckResult>

type ICompiledExtensionSourceChecker =
    inherit IExtensionSourceChecker
    abstract CompileAsync: ExtensionPackageSnapshot * CancellationToken -> Task<ExtensionCompilationResult>
