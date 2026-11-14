#load "lib/TestResults.fsx"
open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Xml.Linq
open Common
open TestResults

let snapshot () =
    Process.GetProcesses()
    |> Array.choose (fun child ->
        use handle = child
        try
            if Regex.IsMatch(handle.ProcessName, "^(testhost|vstest|datacollector)") then Some handle.Id else None
        with :? InvalidOperationException -> None)

let availableRam () =
    if OperatingSystem.IsLinux() && File.Exists("/proc/meminfo") then
        let found = Regex.Match(File.ReadAllText("/proc/meminfo"), @"(?m)^MemAvailable:\s+(\d+) kB")
        if found.Success then jvalue (Int64.Parse(found.Groups[1].Value) * 1024L) else null
    elif OperatingSystem.IsMacOS() then
        let output = capture "vm_stat" []
        let page = Regex.Match(output, @"page size of (\d+) bytes")
        let pages = ["Pages free"; "Pages inactive"; "Pages speculative"] |> List.sumBy (fun label ->
            let found = Regex.Match(output, $@"(?m)^{label}:\s+(\d+)")
            if found.Success then Int64.Parse(found.Groups[1].Value) else 0L)
        if page.Success then jvalue (pages * Int64.Parse(page.Groups[1].Value)) else null
    else
        // Portable approximation on Windows; no PowerShell/CIM dependency.
        jvalue (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)

let property name (path: string) =
    XDocument.Load(path).Descendants() |> Seq.tryFind (fun node -> node.Name.LocalName = name && node.Value <> "") |> Option.map _.Value

let validate (manifest: JsonObject) args (suiteData: JsonNode) (suiteDef: JsonNode) runDir projectName =
    let errors, signals, cases = ResizeArray<string>(), ResizeArray<string>(), ResizeArray<JsonNode>()
    let owners = Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
    let mutable total, passed, failed, skipped, failedOutcome = 0, 0, 0, 0, false
    let batches = items manifest["batches_run"]
    for batch in batches do
        cancellation.Token.ThrowIfCancellationRequested()
        let name = get "name" batch
        let summaryPath = Path.Combine(runDir, if batches.Length = 1 then "summary.json" else $"summary-{name}.json")
        try
            let report = summarize (get "trx" batch) projectName 4000 [Path.Combine(runDir, "console.log")]
            writeJson summaryPath report
            manifest["phases"][$"summarize-{name}"] <- jobj ["exit_code", jvalue 0]
            let counts = report["counts"]
            if intValue counts["results"] = 0 then errors.Add($"batch '{name}': TRX contains zero test results")
            if intValue counts["other"] > 0 then errors.Add($"batch '{name}': unrecognized test outcomes")
            for key in ["results"; "passed"; "failed"; "skipped"] do batch[key] <- counts[key].DeepClone()
            batch["summary_path"] <- jvalue summaryPath
            let entries = items report["cases"] |> List.map (fun row -> jobj ["key", row["key"].DeepClone(); "outcome", row["outcome"].DeepClone()] :> JsonNode)
            batch["case_entries"] <- jarray entries
            for row in entries do
                let key = get "key" row
                match owners.TryGetValue(key) with
                | true, owner when owner <> name -> errors.Add($"case appears in more than one resource batch: {key}")
                | _ -> owners[key] <- name
                cases.Add(row)
            total <- total + intValue counts["results"]
            passed <- passed + intValue counts["passed"]
            failed <- failed + intValue counts["failed"]
            skipped <- skipped + intValue counts["skipped"]
            failedOutcome <- failedOutcome || get "outcome" report["run"] = "Failed"
            for signal in strings report["incomplete_signals"] do signals.Add($"{name}: {signal}")
        with
        | :? OperationCanceledException -> reraise()
        | error ->
            manifest["phases"][$"summarize-{name}"] <- jobj ["exit_code", jvalue 2]
            errors.Add($"batch '{name}': missing, empty or invalid TRX: {error.Message}")
    let keys = cases |> Seq.map (get "key") |> Seq.toList
    writeText (Path.Combine(runDir, "cases.txt")) (String.concat "\n" keys + "\n")
    if has "--expected-cases-file" args then
        let missing, extra = compareMultiset (expectedCases (resolve (arg "--expected-cases-file" "" args))) keys
        if not missing.IsEmpty || not extra.IsEmpty then
            writeJson (Path.Combine(runDir, "case-diff.json")) (jobj ["missing", jvalue missing; "extra", jvalue extra])
            errors.Add($"case multiset mismatch: missing={missing.Length} extra={extra.Length}")
    if not (isNull suiteDef) then
        let required = prop (arg "--suite" "All" args) (prop "suite_required_cases" suiteData)
        let prefixes = if isNull required then [] elif required :? JsonArray then strings required else strings suiteData[text required]
        for prefix in prefixes do
            if not (keys |> List.exists (fun key -> (key.Split([|"::"|], 2, StringSplitOptions.None) |> Array.last).StartsWith(prefix, StringComparison.Ordinal))) then
                errors.Add($"required integration case missing from the union: {prefix}")
        let allow = prop "skip_allowlist" suiteData |> prop "cases" |> strings
        for row in cases do
            let key = get "key" row
            let tail = key.Split([|"::"|], 2, StringSplitOptions.None) |> Array.last
            if get "outcome" row = "NotExecuted" && not (allow |> List.exists (fun prefix -> tail.StartsWith(prefix, StringComparison.Ordinal))) then
                errors.Add($"skipped case outside the manifest allowlist: {key}")
    manifest["results"] <- jobj ["total", jvalue total; "passed", jvalue passed; "failed", jvalue failed; "skipped", jvalue skipped;
                                  "outcome", jvalue (if failedOutcome then "Failed" else "Completed"); "incomplete_signals", jvalue (List.ofSeq signals)]
    let status, reason =
        if errors.Count > 0 then "failed", String.concat "; " errors
        elif signals.Count > 0 then "incomplete", String.concat "; " signals
        elif intValue manifest["exit_code"] <> 0 || failed > 0 || failedOutcome then "failed", "Runner or test results contain failures"
        else "passed", ""
    manifest["status"] <- jvalue status
    manifest["status_reason"] <- nullable reason

entry (fun argv ->
    let args = parse ["--skip-restore"; "--no-build"; "--build-rust-helper"] ["--project"; "--suite"; "--configuration"; "--rid"; "--filter"; "--expected-cases-file"; "--run-id"; "--results-root"; "--suite-manifest"; "--blame-hang-timeout"; "--blame-hang-dump-type"] argv
    if has "help" args then
        printfn "Usage: dotnet fsi scripts/run-tests.fsx -- [--project FILE] [--suite All|Full|Fast|Performance|Diagnostic]\n  [--configuration Debug|Release] [--rid RID] [--filter EXPR] [--skip-restore] [--no-build] [--build-rust-helper]\n  [--expected-cases-file FILE] [--run-id ID] [--results-root .tmp/...] [--suite-manifest FILE]\n  [--blame-hang-timeout 30m] [--blame-hang-dump-type mini|full]"
        0
    else
        let project = resolve (arg "--project" "tests/Patchouli.Tests/Patchouli.Tests.csproj" args)
        if not (File.Exists(project)) then failwith $"Test project not found: {project}"
        let suite = choice "--suite" "All" ["All"; "Full"; "Fast"; "Performance"; "Diagnostic"] args
        let configuration = arg "--configuration" "Debug" args
        let rid = arg "--rid" "" args
        let suitePath = resolve (arg "--suite-manifest" "tests/test-suites.json" args)
        let suiteData = if File.Exists(suitePath) then readJson suitePath else null
        let suiteDef = if suite = "All" then null else prop suite (prop "suites" suiteData)
        if suite <> "All" && isNull suiteDef then failwith $"Suite {suite} is not defined in {suitePath}"
        let batches =
            if suite = "All" then [jobj ["name", jvalue "all"; "filter", jvalue ""; "trx", jvalue "results.trx"] :> JsonNode]
            elif get "mode" suiteDef = "batches" then [
                for name in strings suiteDef["batches"] do
                    let batch = items suiteData["batches"] |> List.find (fun batch -> get "name" batch = name) |> _.DeepClone()
                    batch["trx"] <- jvalue $"results-{name}.trx"
                    batch ]
            else [jobj ["name", jvalue (suite.ToLowerInvariant()); "filter", jvalue (get "filter" suiteDef); "trx", jvalue "results.trx"] :> JsonNode]
        if batches.IsEmpty || batches |> List.exists (fun b -> not (Regex.IsMatch(get "name" b, "^[A-Za-z0-9_-]+$"))) then failwith "Invalid batch plan"
        let timeout = arg "--blame-hang-timeout" (get "blame_hang_timeout" suiteDef) args
        let dumpType = choice "--blame-hang-dump-type" "mini" ["mini"; "full"] args
        let projectName = Path.GetFileNameWithoutExtension(project)
        let assembly = property "AssemblyName" project |> Option.defaultValue projectName
        let framework = property "TargetFramework" project |> Option.orElseWith (fun () -> property "TargetFramework" (resolve "Directory.Build.props")) |> Option.defaultValue "net10.0"
        let defaultId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)
        let runId = arg "--run-id" defaultId args
        if not (Regex.IsMatch(runId, "^[A-Za-z0-9][A-Za-z0-9._-]*$")) then failwith "Run ID must be a simple filename"
        let runDir = insideTmp "Test outputs" (Path.Combine(arg "--results-root" ".tmp/test-runs" args, runId))
        if Directory.Exists(runDir) then failwith $"Run directory already exists; choose a unique run ID: {runDir}"
        if has "--expected-cases-file" args && not (File.Exists(resolve (arg "--expected-cases-file" "" args))) then failwith "Expected cases file not found"
        mkdir runDir
        isolateTemp "tests"
        let git args = capture "git" (["-C"; root] @ args)
        let status = git ["status"; "--porcelain"]
        let manifest = jobj [
            "run_id", jvalue runId; "project", jvalue (arg "--project" "tests/Patchouli.Tests/Patchouli.Tests.csproj" args); "project_path", jvalue project
            "assembly", jvalue assembly; "target_framework", jvalue framework; "suite", jvalue suite; "suite_manifest", jvalue suitePath
            "batches", jarray (batches |> List.map _.DeepClone()); "configuration", jvalue configuration; "rid", jvalue rid
            "filter", jvalue (arg "--filter" "" args); "expected_cases", jvalue (arg "--expected-cases-file" "" args)
            "blame_hang", jvalue timeout; "blame_hang_dump_type", jvalue dumpType
            "git", jobj ["head", jvalue (git ["rev-parse"; "HEAD"]); "dirty_files", jvalue (if status = "" then 0 else status.Split('\n').Length); "workspace_fingerprint", jvalue (hashText (status + "\n" + git ["diff"; "HEAD"; "--binary"]))]
            "environment", jobj ["machine", jvalue Environment.MachineName; "os", jvalue (Runtime.InteropServices.RuntimeInformation.OSDescription)
                                 "processor_count", jvalue Environment.ProcessorCount; "dotnet_sdk", null; "available_ram_bytes", availableRam()]
            "run_dir", jvalue runDir; "trx_path", jvalue (Path.Combine(runDir, "results.trx")); "phases", jobj []; "batches_run", jarray []
            "test_processes", jobj ["before", jvalue (snapshot()); "after", jarray []]; "exit_code", null
            "status", jvalue "running"; "status_reason", null; "started_at", jvalue (utcNow()); "finished_at", null ]
        let save () = manifest["finished_at"] <- jvalue (utcNow()); writeJson (Path.Combine(runDir, "run.json")) manifest
        let phase (name: string) file arguments =
            let watch = Stopwatch.StartNew()
            let mutable code: JsonNode = null
            try
                printfn "[run-tests] %s %s" file (String.concat " " arguments)
                let result = execute root file arguments (Some (Path.Combine(runDir, "console.log"))) false
                code <- jvalue result.ExitCode
                result.ExitCode
            finally
                manifest["phases"][name] <- jobj ["seconds", jvalue (Math.Round(watch.Elapsed.TotalSeconds, 3)); "exit_code", code]
                save()
        save()
        printfn "[run-tests] run %s -> %s" runId runDir
        try
            try
                manifest["environment"]["dotnet_sdk"] <- jvalue (capture "dotnet" ["--version"])
                let ridArgs = if rid = "" then [] else ["-r"; rid]
                if not (has "--skip-restore" args) && phase "restore" "dotnet" (["restore"; project] @ ridArgs) <> 0 then failwith "dotnet restore failed"
                if not (has "--no-build" args) && phase "build" "dotnet" (["build"; project; "-c"; configuration; "--no-restore"] @ ridArgs) <> 0 then failwith "dotnet build failed"
                let baseDir = Path.Combine(Path.GetDirectoryName(project), "bin", configuration, framework)
                let builtDir = [Path.Combine(baseDir, rid); baseDir] |> List.tryFind (fun path -> File.Exists(Path.Combine(path, assembly + ".dll")) && File.Exists(Path.Combine(path, assembly + ".runtimeconfig.json")))
                if has "--no-build" args then
                    manifest["phases"]["precheck"] <- jobj ["exit_code", jvalue (if builtDir.IsSome then 0 else 1); "built_dir", (builtDir |> Option.map jvalue |> Option.defaultValue null)]
                    if builtDir.IsNone then failwith $"NoBuild requested but {assembly}.dll/.runtimeconfig.json not found"
                for path in Option.toList builtDir do manifest["binary_sha256"] <- jvalue (sha256 (Path.Combine(path, assembly + ".dll")))
                if has "--build-rust-helper" args then
                    let code = phase "rust-helper" "cargo" ["build"; "--release"; "--locked"; "--manifest-path"; resolve "sidecar/biblatex-helper/Cargo.toml"]
                    if code <> 0 then failwith "Rust helper build failed"
                    let name = "biblatex-helper" + (if OperatingSystem.IsWindows() then ".exe" else "")
                    let source = resolve ("sidecar/biblatex-helper/target/release/" + name)
                    let output = builtDir |> Option.defaultWith (fun () -> failwith "Cannot stage Rust helper: test output directory not found")
                    File.Copy(source, Path.Combine(output, name), true)
                for batch in batches do
                    let name, filter = get "name" batch, get "filter" batch
                    let filter = match arg "--filter" "" args with "" -> filter | extra when filter = "" -> extra | extra -> $"({filter})&({extra})"
                    let batchTimeout = match get "blame_hang_timeout" batch with "" -> timeout | value -> value
                    let testArgs = ["test"; project; "-c"; configuration; "--no-build"; "--no-restore"] @ ridArgs
                                   @ (if filter = "" then [] else ["--filter"; filter])
                                   @ ["--logger"; "trx;LogFileName=" + get "trx" batch; "--results-directory"; runDir]
                                   @ (if batchTimeout = "" then [] else ["--blame-hang"; "--blame-hang-timeout"; batchTimeout; "--blame-hang-dump-type"; dumpType])
                    let code = phase (if batches.Length = 1 then "test" else "test-" + name) "dotnet" testArgs
                    manifest["batches_run"].AsArray().Add(jobj ["name", jvalue name; "filter", jvalue filter; "trx", jvalue (Path.Combine(runDir, get "trx" batch)); "exit_code", jvalue code; "blame_hang_timeout", nullable batchTimeout; "blame_hang_dump_type", (if batchTimeout = "" then null else jvalue dumpType)])
                    if code <> 0 && isNull manifest["exit_code"] then manifest["exit_code"] <- jvalue code
                if isNull manifest["exit_code"] then manifest["exit_code"] <- jvalue 0
                validate manifest args suiteData suiteDef runDir projectName
            with
            | :? OperationCanceledException -> manifest["status"] <- jvalue "incomplete"; manifest["status_reason"] <- jvalue "Test run interrupted"
            | error -> manifest["status"] <- jvalue "failed"; manifest["status_reason"] <- jvalue error.Message
        finally
            manifest["test_processes"]["after"] <- jvalue (snapshot())
            save()
        printfn "[run-tests] status=%s run_dir=%s" (get "status" manifest) runDir
        if get "status_reason" manifest <> "" then eprintfn "%s" (get "status_reason" manifest)
        if get "status" manifest = "passed" then 0 else 1)
