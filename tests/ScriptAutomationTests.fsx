#load "../scripts/lib/TestResults.fsx"
open System
open System.IO
open Common
open TestResults

let failures = ResizeArray<string>()
let check name condition = if not condition then failures.Add(name)
let fixture name = resolve ("tests/Fixtures/TestRuns/" + name)

let summary name = summarize (fixture name) "Patchouli.Tests" 4000 []
let count (key: string) (report: System.Text.Json.Nodes.JsonNode) = intValue report.["counts"].[key]
let signals (report: System.Text.Json.Nodes.JsonNode) = items report.["incomplete_signals"]

let passed = summary "all-passed.trx"
check "all-passed has results" (count "results" passed > 0)
check "all-passed has no failures" (count "failed" passed = 0)
check "all-passed is complete" (signals passed).IsEmpty
check "failed fixture reports failures" (count "failed" (summary "failed.trx") > 0)
check "skipped fixture reports skips" (count "skipped" (summary "skipped.trx") > 0)
check "empty fixture has no results" (count "results" (summary "empty-results.trx") = 0)
check "truncated fixture is a parse error" (try summary "truncated.trx" |> ignore; false with _ -> true)
check "junit fixture parses" (count "results" (summary "junit-mixed.xml") > 0)

let missing, extra = compareMultiset ["A::x"; "x"] ["A::x"; "B::x"]
check "multiset matches qualified then bare" (missing.IsEmpty && extra.IsEmpty)
let missing2, extra2 = compareMultiset ["x"; "x"] ["A::x"]
check "multiset detects missing duplicate" (missing2.Length = 1 && extra2.IsEmpty)

let entries =
    Directory.EnumerateFiles(resolve "scripts", "*.fsx", SearchOption.AllDirectories)
    |> Seq.filter (fun path -> Path.GetFileName(Path.GetDirectoryName(path)) <> "lib")
    |> Seq.sort |> Seq.toList
for script in entries do
    let result = execute root "dotnet" ["fsi"; "--warnaserror+"; script; "--"; "--help"] None true
    check $"{Path.GetFileName(script)} --help" (result.ExitCode = 0 && result.Stdout.Contains("Usage:"))

if failures.Count = 0 then
    printfn "Script automation checks passed (%d entry points)." entries.Length
    exit 0
else
    for failure in failures do eprintfn "FAILED: %s" failure
    exit 1
