#load "lib/Palettes.fsx"
open Common
open Palettes

entry (fun argv ->
    let args = parse [] [] argv
    if has "help" args then printfn "Usage: dotnet fsi scripts/generate-palettes.fsx"
    else
        let directory = package()
        for scale in scales do
            let path = resolve ($".agents/palettes/radix_{scale}/DESIGN.md")
            writeText path (design scale (steps directory scale false) (steps directory scale true))
            printfn "wrote %s" path
        let path = resolve "src/Patchouli.Host/Themes/UiColorPalettes.Generated.cs"
        writeText path (csharp directory)
        printfn "wrote %s" path
    0)
