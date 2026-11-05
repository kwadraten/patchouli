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
from PIL import Image
from transformers import AutoModelForCausalLM
from transformers.cache_utils import DynamicCache


class LanguageDecoder(torch.nn.Module):
    def __init__(self, llm):
        super().__init__()
        self.model = llm.model
        self.head = llm.lm_head
        self.layers = llm.config.num_hidden_layers

    def forward(self, embeddings, bias, positions, *past):
        cache = DynamicCache()
        for layer in range(self.layers):
            cache.update(past[2 * layer], past[2 * layer + 1], layer)
        hidden = self.model(
            inputs_embeds=embeddings,
            attention_mask=bias,
            position_ids=positions,
            past_key_values=cache,
            use_cache=True,
        ).last_hidden_state
        output = [self.head(hidden[:, -1:, :])]
        for layer in cache.layers:
            output.extend([layer.keys, layer.values])
        return tuple(output)


def attention_bias(sequence: int, past: int):
    bias = torch.zeros(1, 1, sequence, sequence + past, dtype=torch.float32)
    for row in range(sequence):
        bias[..., row, past + row + 1:] = -1e4
    return bias


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True, type=Path)
    parser.add_argument("--image", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--validate-existing", action="store_true")
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
    visual_vocab = int(model.config.visual_vocab_size)
    indicator_count = int(model.indicator_token_indices.numel())
    model.indicator_token_indices.copy_(torch.arange(
        visual_vocab - indicator_count, visual_vocab,
        dtype=torch.long, device=model.indicator_token_indices.device,
    ))

    image = Image.open(args.image).convert("RGB")
    messages = [{
        "role": "user",
        "content": [
            {"type": "image", "image": image},
            {"type": "text", "text": "Recognize the content of the image."},
        ],
    }]
    ids, pixels, grid = model.preprocess_inputs(messages, enable_thinking=False)
    ids, pixels, grid = ids.cuda(), pixels.cuda(), grid.cuda()
    with torch.inference_mode():
        embeddings = model.merge_multimodal(ids, pixels, grid).float()
    decoder = LanguageDecoder(model.llm).eval().float()

    config = model.llm.config
    layers = config.num_hidden_layers
    num_kv = config.num_key_value_heads
    head_dim = config.head_dim or config.hidden_size // config.num_attention_heads
    past_names = [f"past.{layer}.{kind}" for layer in range(layers) for kind in ("key", "value")]
    present_names = [f"present.{layer}.{kind}" for layer in range(layers) for kind in ("key", "value")]
    axes = {
        "embeddings": {1: "sequence"},
        "bias": {2: "sequence", 3: "total"},
        "positions": {1: "sequence"},
    }
    axes.update({name: {2: "past"} for name in past_names})
    axes.update({name: {2: "total"} for name in present_names})
    names = ["embeddings", "bias", "positions", *past_names]
    if not args.validate_existing:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        sample = (
            embeddings[:, :3, :],
            attention_bias(3, 2).cuda(),
            torch.arange(2, 5, device="cuda", dtype=torch.long).unsqueeze(0),
            *(torch.zeros(1, num_kv, 2, head_dim, device="cuda") for _ in past_names),
        )
        torch.onnx.export(
            decoder, sample, str(args.output), dynamo=False,
            opset_version=21, external_data=True, input_names=names,
            output_names=["logits", *present_names], dynamic_axes=axes,
        )
    onnx.checker.check_model(str(args.output))

    options = ort.SessionOptions()
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    session = ort.InferenceSession(str(args.output), sess_options=options,
                                   providers=["CPUExecutionProvider"])
    length = embeddings.shape[1]
    sample = (
        embeddings,
        attention_bias(length, 0).cuda(),
        torch.arange(length, device="cuda", dtype=torch.long).unsqueeze(0),
        *(torch.zeros(1, num_kv, 0, head_dim, device="cuda") for _ in past_names),
    )
    with torch.inference_mode():
        expected = [value.cpu().numpy() for value in decoder(*sample)]
    inputs = {name: value.cpu().numpy() for name, value in zip(names, sample)}
    actual = session.run(None, inputs)
    difference = float(np.max(np.abs(expected[0] - actual[0])))
    expected_token = int(np.argmax(expected[0][0, -1]))
    actual_token = int(np.argmax(actual[0][0, -1]))
    if expected_token != actual_token or difference > 0.05:
        raise AssertionError("OXR text prefill logits differ from PyTorch.")
    cache_difference = max(
        float(np.max(np.abs(a - b))) for a, b in zip(actual[1:], expected[1:])
    )
    if cache_difference > 0.05:
        raise AssertionError("OXR text prefill cache differs from PyTorch.")

    with torch.inference_mode():
        token_input = model.get_wte()(torch.tensor([[actual_token]], device="cuda")).float()
    next_sample = (
        token_input,
        attention_bias(1, length).cuda(),
        torch.tensor([[length]], dtype=torch.long, device="cuda"),
        *(torch.from_numpy(value).cuda() for value in actual[1:]),
    )
    with torch.inference_mode():
        next_expected = decoder(*next_sample)[0].cpu().numpy()
    next_inputs = {name: value.detach().cpu().numpy() for name, value in zip(names, next_sample)}
    next_actual = session.run(None, next_inputs)[0]
    if int(np.argmax(next_expected)) != int(np.argmax(next_actual)):
        raise AssertionError("OXR text decode next token differs from PyTorch.")

    with args.output.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    print(json.dumps({
        "language_onnx_sha256": digest,
        "prompt_tokens": length,
        "layers": layers,
        "vocab_size": actual[0].shape[-1],
        "prefill_logits_max_abs_diff": difference,
        "prefill_cache_max_abs_diff": cache_difference,
        "prefill_next_token": actual_token,
        "decode_next_token": int(np.argmax(next_actual)),
        "decode_logits_max_abs_diff": float(np.max(np.abs(next_expected - next_actual))),
    }))


if __name__ == "__main__":
    main()
