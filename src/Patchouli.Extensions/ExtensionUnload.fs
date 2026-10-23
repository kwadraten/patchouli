namespace Patchouli.Extensions

open System
open System.Collections.Generic
open System.Diagnostics
open System.Reflection
open System.Runtime.Loader

/// Best-effort cleanup of runtime metadata caches after one collectible extension ALC retires.
/// The callback enumeration and null Type[] invocation follow the successful probe in
/// .tmp/ext-spike2/Spike2/Program.fs (clearMetadataCaches, lines 240-257).
module internal ExtensionUnload =
    type private PendingContext =
        { Weak: WeakReference
          PackageId: string
          mutable Warned: bool }

    let private gate = obj ()
    let private pending = ResizeArray<PendingContext>()
    let private maxPendingContexts = 16
    let private metadataUpdateHandlerName = "System.Reflection.Metadata.MetadataUpdateHandlerAttribute"

    let private traceFailure (error: exn) =
        Trace.TraceWarning(
            "Extension unload cleanup failed (" + error.GetType().Name
            + "); restart the application if the retired extension remains active.")

    let private clearMetadataCaches () =
        try
            for assembly in AssemblyLoadContext.Default.Assemblies do
                try
                    for attribute in assembly.GetCustomAttributesData() do
                        if String.Equals(attribute.AttributeType.FullName, metadataUpdateHandlerName, StringComparison.Ordinal) then
                            let handlerType =
                                attribute.ConstructorArguments
                                |> Seq.tryPick (fun argument ->
                                    match argument.Value with
                                    | :? Type as value -> Some value
                                    | _ -> None)
                            match handlerType with
                            | None -> ()
                            | Some handler ->
                                let clearMethods =
                                    handler.GetMethods(BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
                                    |> Array.filter (fun methodInfo ->
                                        if not methodInfo.IsStatic || methodInfo.Name <> "ClearCache" then false
                                        else
                                            let parameters = methodInfo.GetParameters()
                                            parameters.Length = 1 && parameters[0].ParameterType = typeof<Type array>)
                                match clearMethods with
                                | [| clearCache |] ->
                                    try
                                        clearCache.Invoke(null, [| null |]) |> ignore
                                    with error -> traceFailure error
                                | _ -> ()
                with error -> traceFailure error
        with error -> traceFailure error

    let private reportNaturallyAliveContexts () =
        lock gate (fun () ->
            for index = pending.Count - 1 downto 0 do
                let item = pending[index]
                if not item.Weak.IsAlive then
                    pending.RemoveAt index
                elif not item.Warned then
                    item.Warned <- true
                    Trace.TraceWarning(
                        "Retired extension generation '" + item.PackageId
                        + "' remains reachable at a later unload probe (no GC was forced); restart the application if reclamation is required."))

    /// Call only after the generation's lease count reaches zero. This never forces or waits for GC.
    let unload (packageId: string) (context: AssemblyLoadContext) =
        let weakContext = WeakReference(context)
        try
            context.Unload()
        with error -> traceFailure error
        clearMetadataCaches ()
        reportNaturallyAliveContexts ()
        lock gate (fun () ->
            if pending.Count = maxPendingContexts then pending.RemoveAt 0
            pending.Add({ Weak = weakContext; PackageId = packageId; Warned = false }))
