namespace Patchouli.Extensions

open System.Reflection

/// Host-owned libraries available to every extension without package-local DLLs or NuGet restore.
module internal SharedLibraries =
    let private shared = lazy (
        [| typeof<Microsoft.ML.OnnxRuntime.InferenceSession>.Assembly
           typeof<SkiaSharp.SKBitmap>.Assembly
           typeof<Patchouli.Ocr.Engines.NativeOcrSession>.Assembly
           Assembly.Load("System.Numerics.Tensors") |])

    let assemblies () = shared.Value
    let paths () = assemblies () |> Array.map (fun assembly -> assembly.Location)

/// Direct aliases to the host ONNX Runtime API; objects retain their original type identity.
module Onnx =
    type InferenceSession = Microsoft.ML.OnnxRuntime.InferenceSession
    type SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions
    type RunOptions = Microsoft.ML.OnnxRuntime.RunOptions
    type OrtValue = Microsoft.ML.OnnxRuntime.OrtValue
    type NamedOnnxValue = Microsoft.ML.OnnxRuntime.NamedOnnxValue
    type DenseTensor<'T> = Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<'T>

    let createSession (modelPath: string) (options: SessionOptions) =
        new InferenceSession(modelPath, options)

/// Direct aliases to the host SkiaSharp API. Callers own and dispose the objects they create.
module Skia =
    type Bitmap = SkiaSharp.SKBitmap
    type Canvas = SkiaSharp.SKCanvas
    type Image = SkiaSharp.SKImage
    type ImageInfo = SkiaSharp.SKImageInfo
    type Paint = SkiaSharp.SKPaint
    type Color = SkiaSharp.SKColor
    type Rect = SkiaSharp.SKRect

    let createBitmap (width: int) (height: int) = new Bitmap(width, height)
