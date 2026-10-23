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
    | ProbeUnavailable

/// A deliberately small F# script NuGet SDK preflight, not a general SDK locator.
module internal NugetResolverPreflight =
    let candidatePaths(dotnetHostPath: string, runtimeHostPath: string, pathValue: string,
                       programW6432: string, programFiles: string, userProfile: string,
                       pathSeparator: char, isWindows: bool) =
        let executable = if isWindows then "dotnet.exe" else "dotnet"
        let isDotnetHost (path: string) =
            let name = Path.GetFileName path |> Option.ofObj |> Option.defaultValue ""
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

    let private positiveCacheSync = obj ()
    let mutable private positiveCache: (string * DateTimeOffset) option = None

    let private hasSdkListEntry (stream: Stream) (token: CancellationToken) = task {
        let buffer = Array.zeroCreate<byte> 512
        let line = Array.zeroCreate<byte> 512
        let mutable lineLength = 0
        let mutable overlong = false
        let mutable foundSdk = false
        let isSdkEntry () =
            not overlong
            && Regex.IsMatch(Encoding.ASCII.GetString(line, 0, lineLength),
                "^\\s*[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?\\s+\\[[^\\]]+\\]\\s*$",
                RegexOptions.CultureInvariant)
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
        return foundSdk
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
                    let! hasSdk = sdkOutput
                    do! stderrDrain
                    if child.ExitCode <> 0 then return ProbeUnavailable
                    elif hasSdk then return SdkAvailable path
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

    let ensureSdkAsync(token: CancellationToken) : Task<NugetSdkProbeResult> = task {
        token.ThrowIfCancellationRequested()
        let now = DateTimeOffset.UtcNow
        match lock positiveCacheSync (fun () -> positiveCache |> Option.filter (fun (_, expires) -> expires > now)) with
        | Some(path, _) -> return SdkAvailable path
        | None ->
            let environmentValue name = Environment.GetEnvironmentVariable(name) |> Option.ofObj |> Option.defaultValue ""
            let runtimeHostPath =
                let processPath = Environment.ProcessPath |> Option.ofObj |> Option.defaultValue ""
                let runtimeRoot = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                let runtimeHost =
                    let rec parentAt levels (directory: DirectoryInfo option) =
                        match directory, levels with
                        | Some current, 0 -> Some current.FullName
                        | Some current, remaining -> current.Parent |> Option.ofObj |> parentAt (remaining - 1)
                        | None, _ -> None
                    Path.GetFullPath runtimeRoot
                    |> DirectoryInfo
                    |> Some
                    |> parentAt 3
                    |> Option.map (fun root -> Path.Combine(root, if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"))
                    |> Option.defaultValue ""
                let processName = Path.GetFileNameWithoutExtension processPath |> Option.ofObj |> Option.defaultValue ""
                if String.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase) then processPath else runtimeHost
            let candidates = candidatePaths(environmentValue "DOTNET_HOST_PATH", runtimeHostPath, environmentValue "PATH",
                environmentValue "ProgramW6432", environmentValue "ProgramFiles",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.PathSeparator, OperatingSystem.IsWindows())
            let existing = candidates |> Array.filter File.Exists
            if existing.Length = 0 then return DotnetMissing
            else
                // Match FCS's selected host, not any later SDK installation hidden by PATH.
                let! result = probeHost(existing[0], token)
                match result with
                | SdkAvailable path ->
                    lock positiveCacheSync (fun () -> positiveCache <- Some(path, DateTimeOffset.UtcNow.AddMinutes 5.))
                    return SdkAvailable path
                | other -> return other
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
