#load "lib/Common.fsx"
open System.IO
open Common

entry (fun argv ->
    let args = parse ["--check"; "--full-budget-check"; "--ui"; "--ui-budget-check"] ["--profile"; "--iterations"; "--baseline"; "--output-directory"; "--runs"; "--items"; "--pages-per-item"; "--boxes-per-page"] argv
    if has "help" args then
        printfn "Usage: dotnet fsi scripts/run-perf.fsx -- [--profile smoke|full] [--check] [--baseline FILE] [--ui] [--ui-budget-check] [--full-budget-check] [--runs N] [--iterations N] [--items N] [--pages-per-item N] [--boxes-per-page N] [--output-directory .tmp/...]"
        0
    else
        let profile = choice "--profile" "smoke" ["smoke"; "full"] args
        let iterations = match number "--iterations" 0 args with 0 -> (if profile = "full" then 10 else 5) | value -> value
        let output = insideTmp "Performance outputs" (arg "--output-directory" ".tmp/perf" args)
        mkdir output
        isolateTemp "perf-work"
        let invoke suffix =
            let runArgs = ["run"; "--project"; resolve "tests/Patchouli.Performance"; "--"; "--profile"; profile; "--output"; Path.Combine(output, $"{profile}-{suffix}.json"); "--quiet"; "--iterations"; string iterations]
            let runArgs = runArgs @ [for key in ["--items"; "--pages-per-item"; "--boxes-per-page"] do let value = number key 0 args in if value > 0 then yield key; yield string value]
            let runArgs = runArgs @ (if has "--ui" args then ["--ui"] else []) @ (if has "--ui-budget-check" args then ["--enforce-ui-budgets"] else [])
            let runArgs = runArgs @ (if has "--check" args then ["--check"; "--baseline"; resolve (arg "--baseline" ($".agents/perf/baseline.{profile}.json") args)] else [])
            (execute root "dotnet" runArgs None false).ExitCode
        if has "--full-budget-check" args || has "--ui-budget-check" args then
            let runs = number "--runs" 3 args
            if runs = 0 then failwith "--runs must be positive"
            let mutable passed = 0
            for index in 1..runs do
                printfn "[run-perf] budget check run %d of %d (%s)" index runs profile
                if invoke $"budget{index}" = 0 then passed <- passed + 1
            printfn "[run-perf] %d of %d runs were within budget" passed runs
            if passed > 0 then 0 else 1
        else invoke "run")
