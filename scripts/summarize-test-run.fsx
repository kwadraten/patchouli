#load "lib/TestResults.fsx"
open Common
open TestResults

entry (fun argv ->
    let args = parse ["--quiet"] ["--trx"; "--project"; "--expected"; "--log"; "--out"; "--truncate-output"] argv
    if has "help" args then
        printfn "Usage: dotnet fsi scripts/summarize-test-run.fsx -- --trx FILE [--project NAME] [--expected FILE] [--log FILE] [--out FILE] [--quiet] [--truncate-output N]"
        0
    else
        let path = arg "--trx" "" args
        if path = "" then failwith "--trx is required"
        let report = summarize path (arg "--project" "" args) (number "--truncate-output" 4000 args) (values "--log" args)
        let mutable mismatch = false
        if has "--expected" args then
            let expected = arg "--expected" "" args
            let missing, extra = compareMultiset (expectedCases expected) (items report["cases"] |> List.map (get "key"))
            report["expected"] <- jobj ["file", jvalue expected; "missing", jvalue missing; "extra", jvalue extra; "missing_count", jvalue missing.Length; "extra_count", jvalue extra.Length]
            mismatch <- not missing.IsEmpty || not extra.IsEmpty
        if has "--out" args then writeJson (arg "--out" "" args) report
        if not (has "--quiet" args) then
            let status = if intValue report.["counts"].["results"] = 0 then "empty-results" elif mismatch then "case-mismatch" elif not (items report.["incomplete_signals"]).IsEmpty then "incomplete" else "ok"
            let compact = jobj ["status", jvalue status; "outcome", report.["run"].["outcome"] |> fun value -> if isNull value then null else value.DeepClone();
                                "incomplete_signals", report.["incomplete_signals"].DeepClone(); "expected", report.["expected"] |> fun value -> if isNull value then null else value.DeepClone()]
            for key in ["results"; "passed"; "failed"; "skipped"] do compact.[key] <- report.["counts"].[key].DeepClone()
            printfn "%s" (compact.ToJsonString(jsonOptions))
        if intValue report.["counts"].["results"] = 0 || mismatch then 1 else 0)
