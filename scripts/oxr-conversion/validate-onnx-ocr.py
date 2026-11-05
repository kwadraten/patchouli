import argparse
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
from transformers import AutoTokenizer


def session(path: Path):
    options = ort.SessionOptions()
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    options.intra_op_num_threads = 8
    return ort.InferenceSession(str(path), sess_options=options,
                                providers=["CPUExecutionProvider"])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--original-model", type=Path, required=True)
    parser.add_argument("--reference-inputs", type=Path, required=True)
    parser.add_argument("--indicator-data", type=Path, required=True)
    parser.add_argument("--expected-text", default="HELLO OCR 123")
    parser.add_argument("--max-new-tokens", type=int, default=96)
    args = parser.parse_args()

    with np.load(args.reference_inputs, allow_pickle=False) as reference:
        ids = reference["ids"]
        pixels = reference["pixels"]
        grid = reference["grid"]
    if grid.tolist() != [[1, 28, 28]] or pixels.shape != (784, 768):
        raise ValueError("This ONNX vision graph supports only a 448x448 input image.")
    image_session = session(args.models / "oxr-vision-448.onnx")
    embedding_session = session(args.models / "oxr-text-embedding.onnx")
    language_session = session(args.models / "oxr-language.onnx")
    visual = image_session.run(None, {"pixel_values": pixels.astype(np.float32)})[0]
    embeddings = embedding_session.run(
        None, {"input_ids": np.maximum(ids, 0).astype(np.int64)}
    )[0]
    indicator_vectors = np.fromfile(args.indicator_data, dtype=np.float32).reshape(4, 1024)
    visual_positions = ids[0] == -300
    if int(visual_positions.sum()) != visual.shape[0]:
        raise AssertionError("The ONNX visual token count differs from the prompt.")
    embeddings[0, visual_positions] = visual
    for index, indicator_id in enumerate((-301, -302, -303, -304)):
        embeddings[0, ids[0] == indicator_id] = indicator_vectors[index]

    config = json.loads((args.original_model / "config.json").read_text(encoding="utf-8"))
    llm_config = config["llm_config"]
    layers = llm_config["num_hidden_layers"]
    kv_heads = llm_config["num_key_value_heads"]
    head_dim = llm_config.get("head_dim") or (
        llm_config["hidden_size"] // llm_config["num_attention_heads"]
    )
    past = {
        f"past.{layer}.{kind}": np.zeros((1, kv_heads, 0, head_dim), dtype=np.float32)
        for layer in range(layers) for kind in ("key", "value")
    }
    initial_length = ids.shape[1]
    positions = np.arange(initial_length, dtype=np.int64)[None, :]
    bias = np.zeros((1, 1, initial_length, initial_length), dtype=np.float32)
    for row in range(initial_length):
        bias[..., row, row + 1:] = -1e4
    tokenizer = AutoTokenizer.from_pretrained(
        args.original_model, trust_remote_code=True, local_files_only=True
    )
    stop_tokens = {tokenizer.eos_token_id, tokenizer.convert_tokens_to_ids("<|im_end|>")}
    generated = []
    for step in range(args.max_new_tokens):
        results = language_session.run(
            None, {"embeddings": embeddings, "bias": bias, "positions": positions, **past}
        )
        token_id = int(np.argmax(results[0][0, -1]))
        if token_id in stop_tokens:
            break
        generated.append(token_id)
        present = {name.name: value for name, value in zip(language_session.get_outputs(), results)}
        past = {
            name.name: present["present." + name.name.removeprefix("past.")]
            for name in language_session.get_inputs() if name.name.startswith("past.")
        }
        embeddings = embedding_session.run(
            None, {"input_ids": np.array([[token_id]], dtype=np.int64)}
        )[0]
        positions = np.array([[initial_length + step]], dtype=np.int64)
        bias = np.zeros((1, 1, 1, initial_length + step + 1), dtype=np.float32)
    text = tokenizer.decode(generated, skip_special_tokens=True)
    print(json.dumps({
        "recognized_text": text,
        "generated_tokens": len(generated),
        "all_models_onnxruntime_cpu": True,
        "vision_input_shape": list(pixels.shape),
        "expected_text_found": args.expected_text in text,
    }, ensure_ascii=False))
    if args.expected_text not in text:
        raise AssertionError("OXR ONNX did not recognize the sample image.")


if __name__ == "__main__":
    main()
