namespace Patchouli.Extensions

open System
open System.IO
open System.Text.Json
open System.Collections.Generic
open System.Reflection
open System.Security.Cryptography
open System.Text

/// Compiler references come from the running framework manifest, including self-contained deployments.
/// Application dependencies in TPA are deliberately excluded.
module internal RuntimeFrameworkReferences =
    let private references = lazy (
        let runtimeDirectory =
            Path.GetDirectoryName(typeof<obj>.Assembly.Location)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "The runtime framework directory is unavailable.")
        let manifests = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        match AppContext.GetData("APP_CONTEXT_DEPS_FILES") with
        | :? string as paths ->
            for path in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) do
                manifests.Add(path) |> ignore
        | _ -> ()
        manifests.Add(Path.Combine(runtimeDirectory, "Microsoft.NETCore.App.deps.json")) |> ignore
        match Assembly.GetEntryAssembly() |> Option.ofObj with
        | Some entry -> manifests.Add(Path.ChangeExtension(entry.Location, ".deps.json") |> nonNull) |> ignore
        | None -> ()
        let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for manifest in manifests do
            if File.Exists manifest then
                use document = JsonDocument.Parse(File.ReadAllText manifest)
                match document.RootElement.TryGetProperty("targets") with
                | true, targets ->
                    for target in targets.EnumerateObject() do
                        for library in target.Value.EnumerateObject() do
                            if library.Name.StartsWith("Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal)
                               || library.Name.StartsWith("runtimepack.Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal) then
                                match library.Value.TryGetProperty("runtime") with
                                | true, assets ->
                                    for asset in assets.EnumerateObject() do
                                        if asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
                                            names.Add(Path.GetFileName(asset.Name) |> nonNull) |> ignore
                                | _ -> ()
                | _ -> ()
        if names.Count = 0 then
            invalidOp "The .NET runtime framework manifest is unavailable; extension compilation cannot use SDK reference packs."
        names
        |> Seq.map (fun name -> Path.Combine(runtimeDirectory, name))
        |> Seq.filter File.Exists
        |> Seq.append [ typeof<obj>.Assembly.Location; typeof<int option>.Assembly.Location ]
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toArray)

    let paths () = references.Value

    let private compilerIdentity = ref None
    let private compilerIdentityGate = obj()

    // Bump when checker/compiler output semantics change without a public API or package-version change.
    let compilerFormatRevision = 5

    let private identityAssemblyName (assembly: Assembly) =
        assembly.GetName().Name |> Option.ofObj |> Option.defaultWith (fun () -> invalidOp "A public API assembly has no name.")

    let private invariantString (value: obj | null) =
        match value with
        | null -> ""
        | nonNullValue ->
            Convert.ToString(nonNullValue, Globalization.CultureInfo.InvariantCulture)
            |> Option.ofObj
            |> Option.defaultValue ""

    let private nullableString (value: string | null) =
        match value with
        | null -> ""
        | text -> text

    let rec private typeName (value: Type) =
        if value.IsGenericParameter then
            "!" + string value.GenericParameterPosition
        elif value.IsArray then
            typeName (value.GetElementType() |> nonNull) + "[" + String(',', value.GetArrayRank() - 1) + "]"
        elif value.IsByRef then typeName (value.GetElementType() |> nonNull) + "&"
        elif value.IsPointer then typeName (value.GetElementType() |> nonNull) + "*"
        elif value.IsGenericType then
            let definition = value.GetGenericTypeDefinition().FullName |> nullableString
            let arguments = value.GetGenericArguments() |> Array.map typeName |> String.concat ","
            definition + "[" + arguments + "]"
        else value.FullName |> Option.ofObj |> Option.defaultValue value.Name

    let private nullability (info: NullabilityInfo) =
        let rec render (node: NullabilityInfo) =
            let state = string node.ReadState + "/" + string node.WriteState
            let arguments = node.GenericTypeArguments |> Array.map render |> String.concat ","
            let elementInfo = node.ElementType
            let element = if isNull elementInfo then "" else "[" + render (nonNull elementInfo) + "]"
            state + element + "<" + arguments + ">"
        render info

    let private fieldNullability (context: NullabilityInfoContext) (field: FieldInfo) =
        context.Create(field) |> nullability

    let private propertyNullability (context: NullabilityInfoContext) (propertyInfo: PropertyInfo) =
        context.Create(propertyInfo) |> nullability

    let private parameterNullability (context: NullabilityInfoContext) (parameter: ParameterInfo) =
        context.Create(parameter) |> nullability

    let private returnNullability (context: NullabilityInfoContext) (parameter: ParameterInfo) =
        context.Create(parameter) |> nullability

    let private attributeText (attribute: CustomAttributeData) =
        let rec argumentText (argument: CustomAttributeTypedArgument) =
            let valueText =
                match argument.Value with
                | null -> "null"
                | :? IEnumerable<CustomAttributeTypedArgument> as values ->
                    values |> Seq.map argumentText |> String.concat "," |> fun value -> "[" + value + "]"
                | value when argument.ArgumentType.IsEnum -> Convert.ToInt64(value, Globalization.CultureInfo.InvariantCulture).ToString(Globalization.CultureInfo.InvariantCulture)
                | value -> invariantString value
            typeName argument.ArgumentType + "=" + valueText
        let constructor = attribute.ConstructorArguments |> Seq.map argumentText |> String.concat ","
        let named = attribute.NamedArguments
                     |> Seq.map (fun item -> item.MemberName + "=" + argumentText item.TypedValue)
                     |> Seq.sort
                     |> String.concat ","
        let attributeName = attribute.AttributeType.FullName |> Option.ofObj |> Option.defaultValue attribute.AttributeType.Name
        attributeName + "(" + constructor + "){" + named + "}"

    let private attributeLines target (attributes: seq<CustomAttributeData>) =
        attributes |> Seq.map (fun attribute -> "attribute:" + target + ":" + attributeText attribute)

    let private apiMemberLines (assembly: Assembly) =
        let context = NullabilityInfoContext()
        let flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly
        let externallyVisible (memberInfo: MemberInfo) =
            match memberInfo with
            | :? MethodBase as methodBase -> methodBase.IsPublic || methodBase.IsFamily || methodBase.IsFamilyOrAssembly
            | :? FieldInfo as field -> field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly
            | _ -> false
        let propertyVisible (propertyInfo: PropertyInfo) =
            let getter = propertyInfo.GetGetMethod(true)
            let setter = propertyInfo.GetSetMethod(true)
            (getter |> Option.ofObj |> Option.exists externallyVisible) ||
            (setter |> Option.ofObj |> Option.exists externallyVisible)
        let eventVisible (eventInfo: EventInfo) =
            let addMethod = eventInfo.GetAddMethod(true)
            addMethod |> Option.ofObj |> Option.exists externallyVisible
        seq {
            yield "assembly:" + identityAssemblyName assembly
            for publicType in assembly.GetExportedTypes() |> Array.sortBy (fun item -> item.FullName) do
                yield "type:" + typeName publicType + ":base=" + (publicType.BaseType |> Option.ofObj |> Option.map typeName |> Option.defaultValue "")
                yield! attributeLines ("type:" + typeName publicType) publicType.CustomAttributes
                for implemented in publicType.GetInterfaces() |> Array.sortBy typeName do
                    yield "interface:" + typeName publicType + ":" + typeName implemented
                for genericParameter in publicType.GetGenericArguments() do
                    yield "generic:" + typeName publicType + ":" + nullableString genericParameter.Name + ":" + string genericParameter.GenericParameterAttributes + ":" +
                          String.Join(",", genericParameter.GetGenericParameterConstraints() |> Array.map typeName |> Array.sort)
                    yield! attributeLines ("generic:" + typeName publicType + ":" + nullableString genericParameter.Name) genericParameter.CustomAttributes
                for field in publicType.GetFields(flags) |> Array.filter externallyVisible |> Array.sortBy (fun item -> item.Name) do
                    let literal = if field.IsLiteral then invariantString (field.GetRawConstantValue()) else ""
                    yield "field:" + typeName publicType + ":" + field.Name + ":" + typeName field.FieldType + ":" + fieldNullability context field + ":" + literal
                    yield! attributeLines ("field:" + typeName publicType + ":" + field.Name) field.CustomAttributes
                for propertyInfo in publicType.GetProperties(flags) |> Array.filter propertyVisible |> Array.sortBy (fun item -> item.Name) do
                    let index = propertyInfo.GetIndexParameters() |> Array.map (fun (parameter: ParameterInfo) -> typeName parameter.ParameterType + ":" + parameterNullability context parameter) |> String.concat ","
                    yield "property:" + typeName publicType + ":" + propertyInfo.Name + ":" + typeName propertyInfo.PropertyType + ":" + index + ":" + propertyNullability context propertyInfo
                    yield! attributeLines ("property:" + typeName publicType + ":" + propertyInfo.Name) propertyInfo.CustomAttributes
                for eventInfo in publicType.GetEvents(flags) |> Array.filter eventVisible |> Array.sortBy (fun item -> item.Name) do
                    yield "event:" + typeName publicType + ":" + eventInfo.Name + ":" + (eventInfo.EventHandlerType |> Option.ofObj |> Option.map typeName |> Option.defaultValue "")
                    yield! attributeLines ("event:" + typeName publicType + ":" + eventInfo.Name) eventInfo.CustomAttributes
                for methodInfo in publicType.GetMethods(flags) |> Array.filter externallyVisible |> Array.sortBy (fun item -> item.Name + item.ToString()) do
                    let parameters = methodInfo.GetParameters() |> Array.map (fun parameter ->
                        let defaultValue = if parameter.HasDefaultValue then invariantString parameter.RawDefaultValue else ""
                        let nullable = parameterNullability context parameter
                        nullableString parameter.Name + ":" + typeName parameter.ParameterType + ":" + nullable + ":optional=" + string parameter.IsOptional + ":default=" + defaultValue) |> String.concat ";"
                    let returned = returnNullability context methodInfo.ReturnParameter
                    yield "method:" + typeName publicType + ":" + methodInfo.Name + ":" + typeName methodInfo.ReturnType + ":" + returned + ":" + parameters
                    yield! attributeLines ("method:" + typeName publicType + ":" + methodInfo.Name) methodInfo.CustomAttributes
                    yield! (methodInfo.GetParameters() |> Seq.collect (fun parameter -> attributeLines ("parameter:" + typeName publicType + ":" + methodInfo.Name + ":" + string parameter.Position) parameter.CustomAttributes))
                    for genericParameter in methodInfo.GetGenericArguments() do
                        yield "method-generic:" + typeName publicType + ":" + methodInfo.Name + ":" + nullableString genericParameter.Name + ":" + string genericParameter.GenericParameterAttributes + ":" + String.Join(",", genericParameter.GetGenericParameterConstraints() |> Array.map typeName |> Array.sort)
                        yield! attributeLines ("method-generic:" + typeName publicType + ":" + methodInfo.Name + ":" + nullableString genericParameter.Name) genericParameter.CustomAttributes
                for constructor in publicType.GetConstructors(flags) |> Array.filter externallyVisible |> Array.sortBy (fun item -> item.ToString()) do
                    let parameters = constructor.GetParameters() |> Array.map (fun parameter ->
                        let defaultValue = if parameter.HasDefaultValue then invariantString parameter.RawDefaultValue else ""
                        nullableString parameter.Name + ":" + typeName parameter.ParameterType + ":" + parameterNullability context parameter + ":optional=" + string parameter.IsOptional + ":default=" + defaultValue) |> String.concat ";"
                    yield "constructor:" + typeName publicType + ":" + parameters
                    yield! attributeLines ("constructor:" + typeName publicType) constructor.CustomAttributes
                    yield! (constructor.GetParameters() |> Seq.collect (fun parameter -> attributeLines ("constructor-parameter:" + typeName publicType + ":" + string parameter.Position) parameter.CustomAttributes))
        }

    let private packageVersion packageName =
        let entry = Assembly.GetEntryAssembly() |> Option.ofObj |> Option.defaultWith (fun () -> invalidOp "The extension schema tool entry assembly is unavailable.")
        let depsPath = Path.ChangeExtension(entry.Location, ".deps.json") |> nonNull
        if not (File.Exists depsPath) then invalidOp "The extension schema tool dependency manifest is unavailable."
        use document = JsonDocument.Parse(File.ReadAllText depsPath)
        let mutable libraries = Unchecked.defaultof<JsonElement>
        if not (document.RootElement.TryGetProperty("libraries", &libraries)) then
            invalidOp "The extension schema tool dependency manifest has no library identities."
        let prefix = packageName + "/"
        let versions =
            libraries.EnumerateObject()
            |> Seq.choose (fun library -> if library.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then Some(library.Name.Substring(prefix.Length)) else None)
            |> Seq.distinct
            |> Seq.toArray
        if versions.Length <> 1 then invalidOp ("Expected exactly one resolved version for package " + packageName + ".")
        versions[0]

    /// Build-only identity over exported ABI metadata; implementation bytes are deliberately excluded.
    let computeApiIdentity () =
        let hostApi = Patchouli.Extensions.Contracts.ExtensionHostApi.Current
        let fsharpCoreVersion = packageVersion "FSharp.Core"
        let fsharpCompilerServiceVersion = packageVersion "FSharp.Compiler.Service"
        let assemblies =
            [| typeof<Patchouli.Extensions.Contracts.ITranslationExtension>.Assembly
               typeof<Patchouli.Core.Ids.DocumentInstanceId>.Assembly
               Assembly.GetExecutingAssembly() |]
        let material =
            seq {
                yield "patchouli-extension-compiler-format=" + string compilerFormatRevision
                yield "host-api=" + hostApi
                yield "target=net10.0"
                yield "fsharp-core-package=" + fsharpCoreVersion
                yield "fsharp-compiler-service-package=" + fsharpCompilerServiceVersion
                yield "flags=--noframework,--targetprofile:netcore,--langversion:10.0,--checknulls+,--warnaserror+,--optimize+,--crossoptimize-"
                for assembly in assemblies do
                    yield! apiMemberLines assembly
            }
            |> Seq.map (fun line -> line.Length.ToString(Globalization.CultureInfo.InvariantCulture) + ":" + line)
            |> String.concat "\n"
        let bytes = Encoding.UTF8.GetBytes(material)
        let hash = SHA256.HashData(bytes)
        Convert.ToHexString(hash).ToLowerInvariant()

    let fsharpCorePackageVersion () = packageVersion "FSharp.Core"
    let fsharpCompilerServicePackageVersion () = packageVersion "FSharp.Compiler.Service"

    let setCompilerIdentity (identity: string) =
        if String.IsNullOrWhiteSpace identity then invalidArg "identity" "Compiler identity is required."
        lock compilerIdentityGate (fun () -> compilerIdentity := Some identity)

    let private readStampIdentity () =
        let path = Path.Combine(AppContext.BaseDirectory, "ExtensionToolchain.json")
        if not (File.Exists path) then invalidOp "ExtensionToolchain.json is missing; rebuild or repair the extension compiler deployment."
        use document = JsonDocument.Parse(File.ReadAllText path)
        let root = document.RootElement
        let mutable schemaVersion = Unchecked.defaultof<JsonElement>
        let mutable identity = Unchecked.defaultof<JsonElement>
        if not (root.TryGetProperty("schemaVersion", &schemaVersion)) || schemaVersion.GetInt32() <> 1
           || not (root.TryGetProperty("identity", &identity)) then
            invalidOp "ExtensionToolchain.json is invalid; rebuild or repair the extension compiler deployment."
        identity.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "ExtensionToolchain.json has no identity; rebuild or repair the extension compiler deployment.")

    let private stampedIdentity = lazy (readStampIdentity())

    let toolchainIdentity () =
        lock compilerIdentityGate (fun () -> !compilerIdentity |> Option.defaultWith (fun () -> stampedIdentity.Value))

    let compilerFlags () =
        Array.append [| "--noframework"; "--targetprofile:netcore" |]
            (paths () |> Array.map (fun path -> "--reference:" + path))
