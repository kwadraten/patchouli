#r "lib/Patchouli.Ocr.Engines.dll"

open System.Threading
open System.Threading.Tasks
open Patchouli.Core.Results
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Patchouli.Ocr.Engines.Oxr

[<Settings>]
type Settings = {
    [<Field("Model directory", Key = "model-path", Kind = ConfigValueKind.DirectoryPath); Default("");
      ManagedFile("oxr-vision-448.onnx",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/oxr-vision-448.onnx",
          ExpectedBytes = 4783722L, Sha256 = "21693fa5b4cc52e4b8ecdec3dfad45425d563fd938a0ad17534c66529f45562f",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("oxr-vision-448.onnx.data",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/oxr-vision-448.onnx.data",
          ExpectedBytes = 3130974168L, Sha256 = "d8e46e2e061276b18553106fbff7a4e60c374a41f11cd97204381c1833c804da",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("oxr-text-embedding.onnx",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/oxr-text-embedding.onnx",
          ExpectedBytes = 295L, Sha256 = "9a17f7461aff5e2682c646c64a8983d0725a3f61c800e2b7c1e1deb9a71dc2a4",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("oxr-text-embedding.onnx.data",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/oxr-text-embedding.onnx.data",
          ExpectedBytes = 622329856L, Sha256 = "066a9dbb18ff50788d8b3f9f2259ec82d0ec9577f8ba37cb4a4263a06ff9ee45",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("oxr-language.onnx",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/oxr-language.onnx",
          ExpectedBytes = 1335139L, Sha256 = "04d32cfe162a813fb04b9c535eb74c7989a011eedb96077ce17751857b16ade9",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("oxr-language.onnx.data",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/oxr-language.onnx.data",
          ExpectedBytes = 2384199680L, Sha256 = "c4f3d40e82573811af472aeccfefa97d05881efa5463175e7d02564c5d1cc962",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("oxr-layout.onnx",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/oxr-layout.onnx",
          ExpectedBytes = 78605372L, Sha256 = "f5db09a5bd81fb1e419cdfc98d5e1ca0862e6a146c3317876d7f73ac32002e48",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("oxr-indicators.f32",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/oxr-indicators.f32",
          ExpectedBytes = 16384L, Sha256 = "1a319aa1d58d977f63e6c7eb7e4ddcef5f82f2f180a90933f2a5110fedf1409f",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("oxr-prompt.json",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/oxr-prompt.json",
          ExpectedBytes = 386L, Sha256 = "af5b244cc91124a0d84ac396be5cf9e2363430adae138a14d184ddf8e637b1c3",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("tokenizer.json",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/tokenizer.json",
          ExpectedBytes = 11422654L, Sha256 = "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("layout-metadata.json",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/layout-metadata.json",
          ExpectedBytes = 955L, Sha256 = "a1a5552d47f217d585711fe7d8adb89273784fcb4388cd4d23ee288f13f34dc3",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("LICENSE",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/LICENSE",
          ExpectedBytes = 6588L, Sha256 = "4577df88e499926976b390c4ea3992cc91baa9ebac29386f35b557bd3cb4a662",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("NOTICE",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/NOTICE",
          ExpectedBytes = 2170L, Sha256 = "ccb9d9a5c87924b5a27d03b81e1fc5e6ef02031ddf9010d762ced34dc8c3b9c6",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("LICENSES/Apache-2.0.txt",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/LICENSES/Apache-2.0.txt",
          ExpectedBytes = 11558L, Sha256 = "1eb85fc97224598dad1852b5d6483bbcf0aa8608790dcc657a5a2a761ae9c8c6",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models");
      ManagedFile("LICENSES/D-FINE-NOTICES.md",
          "https://huggingface.co/kwadraten/OXR-1.0-ONNX/resolve/f6a1cd8115cd4ff3d34b6e0a7d366677a8f7cee2/LICENSES/D-FINE-NOTICES.md",
          ExpectedBytes = 664L, Sha256 = "29bbcc5bdecc263b437455686435cdf6e9da91d17572e9fd96cbafe5967b5abb",
          Version = "kwadraten/OXR-1.0-ONNX@f6a1cd8", DirectoryKind = "oxr-models")>]
    ModelPath: string
    [<Field("Maximum generated tokens", Key = "max-new-tokens"); Default(512); Range(1.0, 4096.0)>]
    MaxNewTokens: int
}

let private check (config: ExtensionConfigSnapshot) (_settings: Settings)
                  (_resources: IExtensionResources) (_token: CancellationToken) : Task<Result> =
    Task.FromResult(OxrOnnxSession.CheckReadiness(config))

let private run (_settings: Settings) (job: IOcrJob) (_resources: IExtensionResources)
                (token: CancellationToken) : Task =
    task {
        use session = new OxrOnnxSession(job.Info.Config)
        let engine: OcrPageEngine =
            { Recognize = fun _page input image cancellationToken ->
                Task.FromResult(session.Recognize(input, image, cancellationToken)) }
        do! OcrJob.runPages job engine token
    } :> Task

/// <summary>Offline OXR OCR through local layout, vision, and language ONNX graphs.</summary>
/// <patchouli-extension id="ocr.oxr" kind="ocr" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>OXR (ONNX)</name>
/// </patchouli-extension>
let extension : IOcrExtension =
    Extension.createOcrEngine<Settings> (OxrOnnxSession.GetDefinition()) check run
