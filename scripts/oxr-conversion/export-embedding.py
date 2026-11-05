import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"

import numpy as np
import onnx
import onnxruntime as ort
import torch
from transformers import AutoModelForCausalLM


class TextEmbedding(torch.nn.Module):
    def __init__(self, embedding):
        super().__init__()
        self.embedding = embedding

    def forward(self, input_ids):
        return self.embedding(input_ids)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--indicator-output", required=True, type=Path)
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location(
        "oxr_vision_probe", Path(__file__).with_name("export-vision-probe.py")
    )
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    helper.verify_model(args.model_dir)
    model = AutoModelForCausalLM.from_pretrained(
        args.model_dir, trust_remote_code=True, local_files_only=True,
        dtype=torch.bfloat16, attn_implementation="eager",
    ).eval().to("cuda")
    indicators = model.vte.weight[-4:].float().cpu().detach().numpy()
    indicators.tofile(args.indicator_output)
    embedding = TextEmbedding(model.get_wte()).eval().float()
    ids = torch.tensor([[32, 151645, 265]], dtype=torch.long, device="cuda")
    with torch.inference_mode():
        expected = embedding(ids).cpu().numpy()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        embedding, (ids,), str(args.output), dynamo=False,
        opset_version=20, external_data=True,
        input_names=["input_ids"], output_names=["embeddings"],
        dynamic_axes={"input_ids": {1: "sequence"}, "embeddings": {1: "sequence"}},
    )
    onnx.checker.check_model(str(args.output))
    session = ort.InferenceSession(str(args.output), providers=["CPUExecutionProvider"])
    for input_ids in (ids, ids[:, 1:]):
        with torch.inference_mode():
            expected = embedding(input_ids).cpu().numpy()
        actual = session.run(None, {"input_ids": input_ids.cpu().numpy()})[0]
        np.testing.assert_allclose(expected, actual, atol=1e-5, rtol=1e-5)
    with args.output.open("rb") as stream:
        graph_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    with args.indicator_output.open("rb") as stream:
        indicator_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    print(json.dumps({
        "text_embedding_onnx_sha256": graph_hash,
        "indicator_sha256": indicator_hash,
        "indicator_shape": list(indicators.shape),
        "text_embedding_rows": model.get_wte().num_embeddings,
    }))


if __name__ == "__main__":
    main()
