#load "lib/JetBrains.fsx"
open Common
open JetBrains

entry (fun argv ->
    let args = parse ["--use-existing-build"] ["--include"; "--artifacts-path"; "--caches-path"; "--configuration"] argv
    if has "help" args then
        printfn "Usage: dotnet fsi scripts/cleanup-code.fsx -- [--include 'GLOB;GLOB'] [--configuration Debug|Release] [--use-existing-build] [--artifacts-path .tmp/...] [--caches-path .tmp/...]"
    else
        isolateTemp "jetbrains"
        let includeValue = globs "--include" ["src/**/*.cs"; "tests/**/*.cs"; "scripts/**/*.cs"; "src/**/*.axaml"; "tests/**/*.axaml"] args
        let toolArgs = arguments "cleanupcode" args
        run "jb" (["cleanupcode"; resolve "Patchouli.sln"; "--profile=Built-in: Reformat & Apply Syntax Style"; "--include=" + includeValue] @ toolArgs)
    0)
