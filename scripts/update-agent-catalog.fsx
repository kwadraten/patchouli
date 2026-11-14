#load "lib/AgentCatalog.fsx"
open System.IO
open Common
open AgentCatalog

entry (fun argv ->
    let args = parse ["--refresh"; "--check"] ["--output-path"] argv
    if has "help" args then printfn "Usage: dotnet fsi scripts/update-agent-catalog.fsx -- [--refresh] [--check] [--output-path FILE]"; 0
    else
        let existing = if File.Exists(catalogPath) then File.ReadAllText(catalogPath) else ""
        let content = generate (loadRegistry (has "--refresh" args)) existing
        let target = resolve (arg "--output-path" catalogPath args)
        if has "--check" args then
            let check = resolve ".tmp/agent-catalog/AgentCatalog.g.cs.check"
            writeText check content
            if File.Exists(target) && File.ReadAllBytes(target) = utf8.GetBytes(content) then printfn "Agent catalog is up to date: %s" target; 0
            else
                eprintfn "Agent catalog is stale or missing: %s\nGenerated copy: %s" target check
                1
        else writeText target content; printfn "wrote %s" target; 0)
