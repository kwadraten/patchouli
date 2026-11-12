namespace Patchouli.Extensions

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

type internal NugetSdkProbeResult =
    | SdkAvailable of string
    | DotnetMissing
    | RuntimeOnly of string
    | SdkIncompatible of string
    | ProbeUnavailable

/// A deliberately small F# script NuGet SDK preflight, not a general SDK locator.
module internal NugetResolverPreflight =
    // Path helpers report null only when the input is null, but the declared nullability of the
    // return type varies across .NET reference packs; keeping ofObj behind a | null parameter
    // compiles under both annotation generations.
    let private pathOption (value: string | null) = Option.ofObj value

    let candidatePaths(dotnetHostPath: string, runtimeHostPath: string, pathValue: string,
                       programW6432: string, programFiles: string, userProfile: string,
                       pathSeparator: char, isWindows: bool) =
        let executable = if isWindows then "dotnet.exe" else "dotnet"
        let isDotnetHost (path: string) =
            let name = pathOption (Path.GetFileName path) |> Option.defaultValue ""
            String.Equals(name, executable, if isWindows then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal)
        let explicitHost = if String.IsNullOrWhiteSpace dotnetHostPath then [||] else [| dotnetHostPath.Trim().Trim('"') |]
        let runtimeHost =
            if not (String.IsNullOrWhiteSpace runtimeHostPath) && isDotnetHost runtimeHostPath then [| runtimeHostPath |]
            else [||]
        let fromPath =
            if String.IsNullOrWhiteSpace pathValue then [||]
            else
                pathValue.Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun directory -> Path.Combine(directory.Trim().Trim('"'), executable))
        let fallbacks =
            if isWindows then
                [| if not (String.IsNullOrWhiteSpace programW6432) then yield Path.Combine(programW6432, "dotnet", executable)
                   if not (String.IsNullOrWhiteSpace programFiles) then yield Path.Combine(programFiles, "dotnet", executable) |]
            else
                [| yield "/usr/share/dotnet/dotnet"
                   yield "/usr/local/share/dotnet/dotnet"
                   if not (String.IsNullOrWhiteSpace userProfile) then yield Path.Combine(userProfile, ".dotnet", executable) |]
        Array.concat [ explicitHost; runtimeHost; fromPath; fallbacks ]
        |> Array.filter (fun path -> not (String.IsNullOrWhiteSpace path))
        |> Array.map Path.GetFullPath
        |> Array.distinct

    let rec private parentAt levels (directory: DirectoryInfo option) =
        match directory, levels with
        | Some current, 0 -> Some current.FullName
        | Some current, remaining -> current.Parent |> Option.ofObj |> parentAt (remaining - 1)
        | None, _ -> None

    let private runtimeHostPath() =
        let processPath = Environment.ProcessPath |> Option.ofObj |> Option.defaultValue ""
        let runtimeRoot = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
        let runtimeHost =
            Path.GetFullPath runtimeRoot
            |> DirectoryInfo
            |> Some
            |> parentAt 3
            |> Option.map (fun root -> Path.Combine(root, if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"))
            |> Option.defaultValue ""
        let processName = pathOption (Path.GetFileNameWithoutExtension processPath) |> Option.defaultValue ""
        if String.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase) then processPath else runtimeHost

    let private existingDotnetHosts() =
        let environmentValue name = Environment.GetEnvironmentVariable(name) |> Option.ofObj |> Option.defaultValue ""
        candidatePaths(environmentValue "DOTNET_HOST_PATH", runtimeHostPath(), environmentValue "PATH",
            environmentValue "ProgramW6432", environmentValue "ProgramFiles",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.PathSeparator, OperatingSystem.IsWindows())
        |> Array.filter File.Exists

    let private positiveCacheSync = obj ()
    let mutable private positiveCache: (string * string * DateTimeOffset) option = None

    let supportsSdkVersion (version: string) =
        let mutable parsed: Version | null = null
        match Version.TryParse(version.Split('-')[0], &parsed), Option.ofObj parsed with
        | true, Some sdk -> sdk.Major >= Environment.Version.Major
        | _ -> false

    let private hasSdkListEntry (stream: Stream) (token: CancellationToken) = task {
        let buffer = Array.zeroCreate<byte> 512
        let line = Array.zeroCreate<byte> 512
        let mutable lineLength = 0
        let mutable overlong = false
        let mutable foundSdk = false
        let mutable foundCompatibleSdk = false
        let isSdkEntry () =
            if overlong then false
            else
                let entry = Regex.Match(Encoding.ASCII.GetString(line, 0, lineLength),
                    "^\\s*(?<version>[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?)\\s+\\[[^\\]]+\\]\\s*$",
                    RegexOptions.CultureInvariant)
                if entry.Success && supportsSdkVersion entry.Groups["version"].Value then
                    foundCompatibleSdk <- true
                entry.Success
        let mutable reading = true
        while reading do
            let! count = stream.ReadAsync(buffer.AsMemory(), token)
            if count = 0 then reading <- false
            else
                for index = 0 to count - 1 do
                    if buffer[index] = byte '\n' then
                        if isSdkEntry () then foundSdk <- true
                        lineLength <- 0
                        overlong <- false
                    elif lineLength < line.Length then
                        line[lineLength] <- buffer[index]
                        lineLength <- lineLength + 1
                    else overlong <- true
        if isSdkEntry () then foundSdk <- true
        return foundSdk, foundCompatibleSdk
    }

    let private probeHost(path: string, callerToken: CancellationToken) = task {
        callerToken.ThrowIfCancellationRequested()
        let info = ProcessStartInfo(path)
        info.UseShellExecute <- false
        info.CreateNoWindow <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.ArgumentList.Add("--list-sdks")
        use child = new Process(StartInfo = info)
        try
            if not (child.Start()) then return ProbeUnavailable
            else
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 5.)
                use linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeout.Token)
                let sdkOutput = hasSdkListEntry child.StandardOutput.BaseStream linked.Token
                let stderrDrain = child.StandardError.BaseStream.CopyToAsync(Stream.Null, linked.Token)
                try
                    do! child.WaitForExitAsync(linked.Token)
                    let! hasSdk, hasCompatibleSdk = sdkOutput
                    do! stderrDrain
                    if child.ExitCode <> 0 then return ProbeUnavailable
                    elif hasCompatibleSdk then return SdkAvailable path
                    elif hasSdk then return SdkIncompatible path
                    else return RuntimeOnly path
                with :? OperationCanceledException ->
                    if not child.HasExited then
                        try child.Kill(entireProcessTree = true) with _ -> ()
                        try do! child.WaitForExitAsync(CancellationToken.None) with _ -> ()
                    try
                        let! _ = sdkOutput
                        ()
                    with _ -> ()
                    try
                        do! stderrDrain
                    with _ -> ()
                    if callerToken.IsCancellationRequested then return raise (OperationCanceledException callerToken)
                    else return ProbeUnavailable
        with
        | :? OperationCanceledException as error when callerToken.IsCancellationRequested -> return raise error
        | _ -> return ProbeUnavailable
    }

    let selectSdkHostAsync(existing: string array,
                          probe: Func<string, CancellationToken, Task<NugetSdkProbeResult>>,
                          token: CancellationToken) = task {
        let mutable result = ProbeUnavailable
        let mutable selected = false
        for host in existing do
            token.ThrowIfCancellationRequested()
            if not selected then
                let! candidate = probe.Invoke(host, token)
                match candidate with
                | SdkAvailable _ ->
                    selected <- true
                    result <- candidate
                | _ ->
                    match result with
                    | ProbeUnavailable -> result <- candidate
                    | _ -> ()
        return result
    }

    let ensureSdkAsync(token: CancellationToken) : Task<NugetSdkProbeResult> = task {
        token.ThrowIfCancellationRequested()
        let now = DateTimeOffset.UtcNow
        let existing = existingDotnetHosts()
        let scope = String.Join("|", existing)
        match lock positiveCacheSync (fun () -> positiveCache |> Option.filter (fun (key, _, expires) -> key = scope && expires > now)) with
        | Some(_, path, _) -> return SdkAvailable path
        | None ->
            if existing.Length = 0 then return DotnetMissing
            else
                // Pin FCS to the first usable host, even if an earlier PATH entry is runtime-only.
                let! result = selectSdkHostAsync(existing, Func<_, _, _>(fun host ct -> probeHost(host, ct)), token)
                match result with
                | SdkAvailable path ->
                    lock positiveCacheSync (fun () -> positiveCache <- Some(scope, path, DateTimeOffset.UtcNow.AddMinutes 5.))
                | _ -> ()
                return result
    }

    let private normalizeVersion (version: string) =
        let value = version.Trim()
        let withoutBuild = value.Split('+')[0]
        if withoutBuild.Contains('-') then withoutBuild.ToLowerInvariant()
        else
            let parts = withoutBuild.Split('.')
            let mutable last = parts.Length - 1
            while last > 0 && parts[last] = "0" do last <- last - 1
            String.Join(".", parts[0..last])

    let isPackageRestored(packageId: string, version: string, resolvedPackages: seq<string * string>) =
        let normalizedId = packageId.Trim()
        let normalizedVersion = normalizeVersion version
        resolvedPackages
        |> Seq.exists (fun (resolvedId, resolvedVersion) ->
            String.Equals(normalizedId, resolvedId, StringComparison.OrdinalIgnoreCase)
            && String.Equals(normalizedVersion, normalizeVersion resolvedVersion, StringComparison.OrdinalIgnoreCase))

    /// MSBuild environment properties for the compiler process. FCS shells out to `dotnet` for
    /// restore; MSBuild reads environment variables as property fallbacks, and these switches stop
    /// the ephemeral generated restore project from importing ancestor Directory.Build.props,
    /// Directory.Build.targets and Directory.Packages.props (central package management) files.
    let restoreIsolationEnvironment =
        [| "ImportDirectoryBuildProps", "false"
           "ImportDirectoryBuildTargets", "false"
           "ImportDirectoryPackagesProps", "false"
           "ManagePackageVersionsCentrally", "false" |]

    let applyRestoreIsolationEnvironment() =
        for name, value in restoreIsolationEnvironment do
            Environment.SetEnvironmentVariable(name, value)
