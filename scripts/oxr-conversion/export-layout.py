import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch


SOURCE_COMMIT = "c1a1dcb312ee9f691404f127785510453c0d9350"
CHECKPOINT_SHA256 = "dff78ccf18439955362ca44a69eb28fb0b40222a7d278860589682de0176dc17"
CHECKPOINT_BYTES = 78791307


def validate_source(source: Path, checkpoint: Path) -> None:
    commit = subprocess.check_output(
        ["git", "-C", str(source), "rev-parse", "HEAD"], text=True
    ).strip()
    if commit != SOURCE_COMMIT:
        raise ValueError("The OXR layout source checkout is not the reviewed commit.")
    if checkpoint.stat().st_size != CHECKPOINT_BYTES:
        raise ValueError("The OXR layout checkpoint has an unexpected size.")
    with checkpoint.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != CHECKPOINT_SHA256:
        raise ValueError("The OXR layout checkpoint is not the pinned release.")


def match_detections(expected, actual, minimum_score: float) -> tuple[int, float, float]:
    expected_indexes = np.flatnonzero(expected[2][0] >= minimum_score)
    actual_indexes = np.flatnonzero(actual[2][0] >= minimum_score)
    if not len(expected_indexes) or len(expected_indexes) != len(actual_indexes):
        raise AssertionError("The exported layout has a different number of high-confidence boxes.")

    remaining = set(expected_indexes.tolist())
    maximum_box_difference = 0.0
    maximum_score_difference = 0.0
    for actual_index in actual_indexes:
        compatible = [
            (float(np.max(np.abs(expected[1][0, index] - actual[1][0, actual_index]))),
             abs(float(expected[2][0, index] - actual[2][0, actual_index])), index)
            for index in remaining if expected[0][0, index] == actual[0][0, actual_index]
        ]
        if not compatible:
            raise AssertionError("The exported layout has an unmatched high-confidence category.")
        box_difference, score_difference, selected = min(compatible)
        if box_difference > 1 or score_difference > 1e-3:
            raise AssertionError("The exported high-confidence layout predictions differ from PyTorch.")
        remaining.remove(selected)
        maximum_box_difference = max(maximum_box_difference, box_difference)
        maximum_score_difference = max(maximum_score_difference, score_difference)
    return len(actual_indexes), maximum_box_difference, maximum_score_difference


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--image", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--validate-existing", action="store_true")
    args = parser.parse_args()
    validate_source(args.source, args.checkpoint)

    sys.path.insert(0, str(args.source / "services" / "layout_analyze"))
    from dfine_la.model.model import load_model
    from dfine_la.preprocess import decode_images, preprocess_images

    image = decode_images([args.image.read_bytes()])
    images, sizes = preprocess_images(image)
    model = load_model(args.checkpoint, torch.device("cpu"))
    with torch.inference_mode():
        expected = [value.numpy() for value in model(images, sizes)]

    if not args.validate_existing:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        torch.onnx.export(
            model,
            (images, sizes),
            str(args.output),
            input_names=["images", "original_sizes"],
            output_names=["labels", "boxes", "scores"],
            opset_version=18,
            dynamo=False,
        )
    onnx.checker.check_model(str(args.output))
    session = ort.InferenceSession(str(args.output), providers=["CPUExecutionProvider"])
    actual = session.run(None, {"images": images.numpy(), "original_sizes": sizes.numpy()})
    count, box_difference, score_difference = match_detections(expected, actual, 0.03)
    if not np.allclose(actual[2], expected[2], rtol=1e-3, atol=1e-3):
        raise AssertionError("The exported layout scores differ from PyTorch.")

    with args.output.open("rb") as stream:
        output_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    print(json.dumps({
        "checkpoint_sha256": CHECKPOINT_SHA256,
        "onnx_sha256": output_hash,
        "image_size": list(images.shape),
        "output_shapes": [list(value.shape) for value in actual],
        "matched_confidence_threshold": 0.03,
        "matched_count": count,
        "maximum_matched_box_difference_pixels": box_difference,
        "maximum_matched_score_difference": score_difference,
        "maximum_sorted_score_difference": float(np.max(np.abs(actual[2] - expected[2]))),
    }))


if __name__ == "__main__":
    main()
