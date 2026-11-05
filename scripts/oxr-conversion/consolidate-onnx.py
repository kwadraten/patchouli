import argparse
import hashlib
import json
from pathlib import Path

import onnx


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    shard = args.output.with_name(args.output.name + ".data")
    if args.output.exists() or shard.exists():
        raise ValueError("The consolidated ONNX destination already exists.")

    model = onnx.load(str(args.source), load_external_data=True)
    onnx.save_model(
        model, str(args.output), save_as_external_data=True,
        all_tensors_to_one_file=True, location=shard.name, size_threshold=0,
    )
    onnx.checker.check_model(str(args.output))
    manifest = {
        "graph": args.output.name,
        "graph_bytes": args.output.stat().st_size,
        "graph_sha256": digest(args.output),
        "external_data": shard.name,
        "external_data_bytes": shard.stat().st_size,
        "external_data_sha256": digest(shard),
    }
    print(json.dumps(manifest))


if __name__ == "__main__":
    main()
