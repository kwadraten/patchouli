#load "lib/JetBrains.fsx"
open Common
open JetBrains

entry (fun argv ->
    let args = parse ["--use-existing-build"] ["--exclude"; "--artifacts-path"; "--caches-path"; "--configuration"] argv
    if has "help" args then
        printfn "Usage: dotnet fsi scripts/inspect-code.fsx -- [--exclude 'GLOB;GLOB'] [--configuration Debug|Release] [--use-existing-build] [--artifacts-path .tmp/...] [--caches-path .tmp/...]"
        0
    else
        isolateTemp "jetbrains"
        let exclude = globs "--exclude" ["src/**/*.fs"; "tests/**/*.fs"; "src/**/*.fsx"; "tests/**/*.fsx"] args
        let toolArgs = arguments "inspectcode" args
        let reportPath = resolve ".tmp/inspectcode.sarif"
        run "jb" (["inspectcode"; resolve "Patchouli.sln"; "--output=" + reportPath; "--format=Sarif"; "--exclude=" + exclude] @ toolArgs)
        let issues = readJson reportPath |> prop "runs" |> items |> List.collect (prop "results" >> items) |> List.filter blocking
        if issues.IsEmpty then printfn "InspectCode passed. Report: %s" reportPath; 0
        else
            for issue in issues do printfn "%s" (issue.ToJsonString(jsonOptions))
            eprintfn "InspectCode found %d blocking issue(s). Report: %s" issues.Length reportPath
            1)
