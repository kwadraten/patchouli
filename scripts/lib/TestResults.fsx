#load "Common.fsx"

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Xml.Linq
open Common

let elements name (element: XElement) =
    if isNull element then [] else element.Elements() |> Seq.filter (fun child -> child.Name.LocalName = name) |> Seq.toList
let child name element = elements name element |> List.tryHead |> Option.defaultValue null
let attribute name (element: XElement) =
    if isNull element then "" else element.Attribute(XName.Get(name)) |> Option.ofObj |> Option.map _.Value |> Option.defaultValue ""
let content (element: XElement) = if isNull element then "" else element.Value
let nullable value = if String.IsNullOrEmpty(value) then null else jvalue value
let truncate limit (value: string) =
    if limit > 0 && value.Length > limit then value.Substring(0, limit) + $"...[truncated {value.Length - limit} chars]" else value
let duration (value: string) =
    match TimeSpan.TryParse(value, CultureInfo.InvariantCulture), Double.TryParse(value, CultureInfo.InvariantCulture) with
    | (true, span), _ -> jvalue span.TotalSeconds
    | _, (true, seconds) -> jvalue seconds
    | _ -> null
let caseKey project name = if String.IsNullOrEmpty(project) then name else project + "::" + name

let compareMultiset (expected: string list) (actual: string list) =
    let counts = Dictionary<string, int>(StringComparer.Ordinal)
    for key in actual do counts[key] <- (match counts.TryGetValue(key) with true, count -> count | _ -> 0) + 1
    let missing = ResizeArray<string>()
    // Fully-qualified entries claim cases before bare names, regardless of file order.
    for entry in List.sortBy (fun (key: string) -> if key.Contains("::") then 0 else 1) expected do
        let hit = counts.Keys |> Seq.tryFind (fun key -> counts[key] > 0 && (if entry.Contains("::") then key = entry else key.Split([|"::"|], 2, StringSplitOptions.None) |> Array.last = entry))
        match hit with Some key -> counts[key] <- counts[key] - 1 | None -> missing.Add(entry)
    let extra = [for KeyValue(key, count) in counts do for _ in 1..count do key]
    List.ofSeq missing, extra

let expectedCases path =
    File.ReadAllLines(path) |> Array.map _.Trim() |> Array.filter (fun line -> line <> "" && not (line.StartsWith("#"))) |> Array.toList

let parseTrx project limit (root: XElement) =
    let definitions = Dictionary<string, XElement * XElement>(StringComparer.Ordinal)
    for unitTest in elements "UnitTest" (child "TestDefinitions" root) do
        let method = child "TestMethod" unitTest
        for execution in elements "Execution" unitTest do definitions[attribute "id" execution] <- unitTest, method
        if attribute "id" unitTest <> "" then definitions.TryAdd(attribute "id" unitTest, (unitTest, method)) |> ignore
    let rec emit parent (result: XElement) =
        let execution = attribute "executionId" result
        let definition =
            match definitions.TryGetValue(execution), definitions.TryGetValue(attribute "testId" result) with
            | (true, pair), _ | _, (true, pair) -> pair
            | _ -> null, null
        let unitTest, method = definition
        let name = match attribute "testName" result with "" -> attribute "name" method | value -> value
        let output = child "Output" result
        let error = child "ErrorInfo" output
        let row = jobj [
            "key", jvalue (caseKey project name); "project", nullable project; "test_name", jvalue name
            "method", nullable (attribute "name" method); "class_name", nullable (attribute "className" method)
            "test_id", nullable (match attribute "testId" result with "" -> attribute "id" unitTest | value -> value)
            "execution_id", nullable execution; "parent_execution_id", nullable parent
            "outcome", nullable (attribute "outcome" result); "duration_s", duration (attribute "duration" result)
            "start", nullable (attribute "startTime" result); "end", nullable (attribute "endTime" result)
            "error", (if isNull error then null else jobj ["message", nullable (content (child "Message" error) |> truncate limit); "stack_trace", nullable (content (child "StackTrace" error) |> truncate limit)])
            "stdout", nullable (content (child "StdOut" output) |> truncate limit)
            "stderr", nullable (content (child "StdErr" output) |> truncate limit) ]
        (row :> JsonNode) :: (elements "UnitTestResult" (child "InnerResults" result) |> List.collect (emit execution))
    let cases = elements "UnitTestResult" (child "Results" root) |> List.collect (emit "")
    let resultSummary = child "ResultSummary" root
    let counters = jobj [
        let node = child "Counters" resultSummary
        if not (isNull node) then
            for attr in node.Attributes() do
                match Int32.TryParse(attr.Value) with true, value -> attr.Name.LocalName, jvalue value | _ -> () ]
    let warnings = elements "RunInfo" (child "RunInfos" resultSummary) |> List.map (fun info -> content (child "Text" info))
    let times = jobj [
        let node = child "Times" root
        if not (isNull node) then for attr in node.Attributes() do attr.Name.LocalName, jvalue attr.Value ]
    let run = jobj ["format", jvalue "vstest-trx"; "id", nullable (attribute "id" root); "name", nullable (attribute "name" root)
                    "run_user", nullable (attribute "runUser" root); "times", times; "outcome", nullable (attribute "outcome" resultSummary)]
    run, counters, warnings, cases

let parseJunit project limit (root: XElement) =
    let suites = if root.Name.LocalName = "testsuite" then [root] else root.Descendants() |> Seq.filter (fun node -> node.Name.LocalName = "testsuite") |> Seq.toList
    let cases = [
        for suite in suites do
            for test in elements "testcase" suite do
                let className = match attribute "classname" test with "" -> attribute "name" suite | value -> value
                let name = attribute "name" test
                let testName = if className = "" then name else className + "." + name
                let error = match child "failure" test with null -> child "error" test | node -> node
                let outcome = if not (isNull error) then (if error.Name.LocalName = "error" then "Error" else "Failed") elif not (isNull (child "skipped" test)) then "NotExecuted" else "Passed"
                jobj ["key", jvalue (caseKey project testName); "project", nullable project; "test_name", jvalue testName
                      "class_name", nullable className; "method", jvalue name; "test_id", null; "execution_id", null
                      "parent_execution_id", null; "outcome", jvalue outcome; "duration_s", duration (attribute "time" test)
                      "start", null; "end", null; "stdout", nullable (content (child "system-out" test) |> truncate limit)
                      "stderr", nullable (content (child "system-err" test) |> truncate limit)
                      "error", (if isNull error then null else jobj ["message", nullable ((attribute "message" error + " " + content error).Trim() |> truncate limit)])] :> JsonNode ]
    let total = suites |> List.sumBy (fun suite -> match Int32.TryParse(attribute "tests" suite) with true, value -> value | _ -> 0)
    let run = jobj ["format", jvalue "junit-xml"; "id", null; "name", nullable (attribute "name" root); "run_user", null; "times", jobj []; "outcome", null]
    let counters = jobj ["total", jvalue (if total = 0 then cases.Length else total); "executed", jvalue cases.Length]
    run, counters, [], cases

let summarize (path: string) project limit (logs: string list) =
    let root = XDocument.Load(path).Root
    let run, counters, warnings, cases =
        match root.Name.LocalName with
        | "TestRun" -> parseTrx project limit root
        | "testsuite" | "testsuites" -> parseJunit project limit root
        | _ -> failwith $"Unrecognized test result format: {path}"
    let count outcome = cases |> List.filter (fun row -> get "outcome" row = outcome) |> List.length
    let passed, failed, skipped = count "Passed", count "Failed" + count "Error", count "NotExecuted"
    let counts = jobj ["results", jvalue cases.Length; "distinct_cases", jvalue (cases |> List.map (get "key") |> List.distinct |> List.length)
                       "passed", jvalue passed; "failed", jvalue failed; "skipped", jvalue skipped
                       "other", jvalue (cases.Length - passed - failed - skipped)
                       "by_outcome", jobj (cases |> List.countBy (get "outcome") |> List.map (fun (key, count) -> key, jvalue count))]
    let signals = ResizeArray<string>()
    let outcome = get "outcome" run
    if outcome <> "" && outcome <> "Completed" && outcome <> "Failed" then signals.Add($"run outcome is '{outcome}'")
    let total, executed = intValue counters["total"], intValue counters["executed"]
    if cases.Length < total then signals.Add($"results contain {cases.Length}/{total} declared cases - run truncated")
    elif not (isNull counters["executed"]) && executed + skipped < total then signals.Add($"counters show {executed} executed and {skipped} skipped of {total} - run truncated")
    for warning in warnings do
        if Regex.IsMatch(warning, @"\b(?:test run|test execution|test host|testhost(?:\.exe)?|test session)\s+(?:(?:was|has been|is)\s+)?(?:aborted|cancelled|canceled|terminated|timed out)\b", RegexOptions.IgnoreCase) then signals.Add("run warning: " + warning)
    jobj ["source", jobj ["path", jvalue (Path.GetFullPath(path)); "format", run["format"].DeepClone(); "size_bytes", jvalue (FileInfo(path).Length); "sha256", jvalue (sha256 path)]
          "project", nullable project; "run", run; "counters", counters; "counts", counts; "run_warnings", jvalue warnings
          "incomplete_signals", jvalue (List.ofSeq signals); "expected", null; "cases", jarray cases
          "logs", jarray [for log in logs do if File.Exists(log) then jobj ["path", jvalue log; "size_bytes", jvalue (FileInfo(log).Length); "sha256", jvalue (sha256 log)] else jobj ["path", jvalue log; "missing", jvalue true]]
          "generated_at", jvalue (utcNow())]
