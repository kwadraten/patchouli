#load "lib/Downloads.fsx"
open System.IO
open Common
open Downloads

entry (fun argv ->
    let args = parse [] ["--model-directory"] argv
    if has "help" args then printfn "Usage: dotnet fsi scripts/prepare-ovisocr3-model.fsx -- --model-directory DIR"
    else
        let directory = arg "--model-directory" "" args
        if directory = "" then failwith "--model-directory is required"
        let directory = Path.GetFullPath(directory)
        mkdir directory
        let onnx = "https://huggingface.co/arlenecc/OvisOCR3-ONNX-int4/resolve/e654df1eed2c3b4a902eac7b458bddd7cb338e1b/onnx/"
        let source = "https://huggingface.co/ATH-MaaS/OvisOCR3/resolve/4833d5d2afa6e769ee933f024504d67ace80a24c/"
        verifiedFile directory "ovisocr3_int4_single.onnx" (onnx + "ovisocr3_int4_single.onnx") 1216759027L "7bafbef06e85847963d46fe520ba9767d18c7d2abdad91a25ce2f036558bcb3e" None
        verifiedFile directory "tokenizer.json" (source + "tokenizer.json") 12807982L "5f9e4d4901a92b997e463c1f46055088b6cca5ca61a6522d1b9f64c4bb81cb42" None
        verifiedFile directory "document_structured.txt" (source + "prompts/document_structured.txt") 1110L "1b6139ec72888e087c8af75f2e2dfb78b425e15ae1c1529940b96137613d1db1" None
        verifiedFile directory "visual_pos_embed.bf16" (source + "model.safetensors") 3538944L "5f6b1b11dc866dc90f773a3e95c7b0ae077cd1744161bccbdc1302050986c287" (Some (1702491584L, 1706030527L))
    0)
