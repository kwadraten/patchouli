import argparse
import hashlib
import json
import os
from pathlib import Path

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"

import numpy as np
import onnx
import onnxruntime as ort
import torch
from PIL import Image
from transformers import AutoModelForCausalLM
from transformers.utils import is_flash_attn_2_available


EXPECTED_FILES = {
    "model.safetensors": (
        3068857520,
        "795bb2f1faac579f58829913f2c606202531a96c1203e19ef7a70b103fb7ea51",
    ),
    "tokenizer.json": (
        11422654,
        "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4",
    ),
    "configuration_oxr.py": (
        4183,
        "79b82be9ab046f095cda04a79b5cba3e0dcb4063759f3c11e39485b2242a2c50",
    ),
    "modeling_oxr.py": (
        45406,
        "3c3561057eb1f2ac8bea3da397e0f3ba959abcad9c8ff61a8b7b723f5bf56042",
    ),
    "config.json": (
        2794,
        "758481181d106aef0a0a22c6059f2ab2a224ec1cba2b2c8d8255cb3ce99a0b24",
    ),
}


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_model(directory: Path) -> None:
    for name, (length, expected_hash) in EXPECTED_FILES.items():
        path = directory / name
        if not path.is_file() or path.stat().st_size != length or digest(path) != expected_hash:
            raise ValueError(f"OXR model file {name} does not match the reviewed release.")
    if not (directory / "LICENSE").is_file():
        raise ValueError("The OXR model license is missing.")


class VisionExport(torch.nn.Module):
    def __init__(self, model, grid: torch.Tensor) -> None:
        super().__init__()
        self.visual = model.visual_tokenizer
        self.embedding = model.vte
        self.register_buffer("grid", grid)

    def forward(self, pixels: torch.Tensor) -> torch.Tensor:
        return self.embedding(self.visual(pixels, self.grid))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True, type=Path)
    parser.add_argument("--image", required=True, type=Path)
    parser.add_argument("--expected-text", default="HELLO OCR 123")
    parser.add_argument("--export-vision", type=Path)
    parser.add_argument("--inspect-inputs", action="store_true")
    parser.add_argument("--capture-inputs", type=Path)
    args = parser.parse_args()
    verify_model(args.model_dir)
    if not torch.cuda.is_available() or not torch.cuda.is_bf16_supported():
        raise RuntimeError("The OXR recognition experiment requires a BF16-capable NVIDIA GPU.")
    if is_flash_attn_2_available() and args.export_vision:
        raise RuntimeError("The OXR vision exporter requires a runtime without FlashAttention 2.")

    model = AutoModelForCausalLM.from_pretrained(
        args.model_dir, trust_remote_code=True, local_files_only=True,
        dtype=torch.bfloat16, attn_implementation="eager",
    ).eval().to("cuda")
    visual_vocab_size = int(model.config.visual_vocab_size)
    indicator_count = int(model.indicator_token_indices.numel())
    expected_indicators = torch.arange(
        visual_vocab_size - indicator_count, visual_vocab_size,
        dtype=torch.long, device=model.indicator_token_indices.device,
    )
    repaired_indicator_buffer = not torch.equal(model.indicator_token_indices, expected_indicators)
    if repaired_indicator_buffer:
        model.indicator_token_indices.copy_(expected_indicators)
    image = Image.open(args.image).convert("RGB")
    messages = [{
        "role": "user",
        "content": [
            {"type": "image", "image": image},
            {"type": "text", "text": "Recognize the content of the image."},
        ],
    }]
    ids, pixels, grid = model.preprocess_inputs(messages, enable_thinking=False)
    if args.capture_inputs:
        np.savez_compressed(args.capture_inputs,
                            ids=ids.cpu().numpy(),
                            pixels=pixels.cpu().numpy(),
                            grid=grid.cpu().numpy())
        print(json.dumps({"saved_reference_inputs": str(args.capture_inputs),
                          "pixel_shape": list(pixels.shape),
                          "token_count": int(ids.shape[1])}))
        return
    ids, pixels, grid = ids.cuda(), pixels.cuda(), grid.cuda()
    if args.inspect_inputs:
        positive = ids[ids >= 0]
        print(json.dumps({
            "tokenizer_size": len(model.text_tokenizer),
            "llm_embedding_rows": model.get_wte().weight.shape[0],
            "visual_embedding_rows": int(model.vte.weight.shape[0]),
            "visual_indicator_ids": model.indicator_token_indices.cpu().tolist(),
            "minimum_token_id": int(ids.min()),
            "maximum_token_id": int(positive.max()),
            "negative_token_ids": sorted(set(ids[ids < 0].cpu().view(-1).tolist())),
            "pixel_shape": list(pixels.shape),
            "grid": grid.cpu().tolist(),
            "vision_position_rows": int(model.visual_tokenizer.vit.vision_model.embeddings.position_embedding.weight.shape[0]),
        }))
        return
    with torch.inference_mode():
        result = model.generate(
            inputs=ids, pixel_values=pixels, grid_thws=grid,
            max_new_tokens=64, do_sample=False,
        )
    text = model.text_tokenizer.decode(result[0], skip_special_tokens=True)
    if args.expected_text not in text:
        raise AssertionError("OXR PyTorch did not recognize the expected text on the sample image.")

    report = {
        "model_sha256": EXPECTED_FILES["model.safetensors"][1],
        "reinitialized_nonpersistent_indicator_buffer": repaired_indicator_buffer,
        "input_pixel_shape": list(pixels.shape),
        "image_grid": grid.cpu().tolist(),
        "prompt_token_count": int(ids.shape[-1]),
        "recognized_expected_text": True,
    }
    if args.export_vision:
        wrapper = VisionExport(model, grid).eval().float()
        pixels = pixels.float()
        with torch.inference_mode():
            expected = wrapper(pixels).cpu().numpy()
        args.export_vision.parent.mkdir(parents=True, exist_ok=True)
        torch.onnx.export(
            wrapper, (pixels,), str(args.export_vision),
            input_names=["pixel_values"], output_names=["visual_embeddings"],
            opset_version=21, dynamo=False, external_data=True,
        )
        onnx.checker.check_model(str(args.export_vision))
        session = ort.InferenceSession(str(args.export_vision), providers=["CPUExecutionProvider"])
        actual = session.run(None, {"pixel_values": pixels.cpu().numpy()})[0]
        if not np.allclose(actual, expected, atol=2e-3, rtol=2e-3):
            raise AssertionError("The OXR vision ONNX embeddings differ from PyTorch.")
        report.update({
            "vision_onnx_sha256": digest(args.export_vision),
            "vision_output_shape": list(actual.shape),
            "vision_max_absolute_difference": float(np.max(np.abs(actual - expected))),
            "vision_grid_is_fixed": True,
        })
    print(json.dumps(report))


if __name__ == "__main__":
    main()
