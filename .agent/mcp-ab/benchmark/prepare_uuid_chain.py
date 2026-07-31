#!/usr/bin/env python3
"""Prepare a Patchouli recipe from gkamradt/needle-in-a-haystack."""
import argparse
import io
import json
import random
import urllib.request
import uuid
import zipfile
from pathlib import Path

REPOSITORY = "gkamradt/needle-in-a-haystack"
DEFAULT_REF = "021385d68d3202e37893e9d3cd29011c569abe30"
DEFAULT_SOURCE = "needlehaystack/PaulGrahamEssays/"


def uuid_chain(length, seed):
    rng = random.Random(seed)
    chain = [str(uuid.UUID(int=rng.getrandbits(128), version=4)) for _ in range(length)]
    return chain, [f"{left} maps to {right}." for left, right in zip(chain, chain[1:])]


def download_sources(ref):
    url = f"https://github.com/{REPOSITORY}/archive/{ref}.zip"
    with urllib.request.urlopen(url, timeout=120) as response:
        archive = zipfile.ZipFile(io.BytesIO(response.read()))
        files = []
        for name in sorted(archive.namelist()):
            if not name.startswith(DEFAULT_SOURCE) and "/" + DEFAULT_SOURCE not in name:
                continue
            if not name.endswith(".txt"):
                continue
            text = archive.read(name).decode("utf-8")
            files.append({"name": Path(name).name, "text": text})
        if not files:
            raise RuntimeError("the pinned repository contains no PaulGrahamEssays text files")
        return files


def build_recipe(args):
    source_files = download_sources(args.ref)
    source_words = [(file["name"], file["text"].split()) for file in source_files]
    if not any(words for _, words in source_words):
        raise RuntimeError("the source corpus is empty")
    documents, remaining = [], args.context_words
    source_index = 0
    while remaining > 0:
        name, words = source_words[source_index % len(source_words)]
        source_index += 1
        selected = words[:remaining]
        if not selected:
            continue
        documents.append({"title": "Paul Graham Essay: " + name, "source_file": name,
                          "pages": [selected[index:index + args.page_words] for index in range(0, len(selected), args.page_words)]})
        remaining -= len(selected)
    pages = [(document_index, page_index)
             for document_index, document in enumerate(documents)
             for page_index, _ in enumerate(document["pages"])]
    chain, links = uuid_chain(args.chain_length, args.seed)
    placements = []
    for index, link in enumerate(links):
        flattened_index = max(0, min(round((index + 1) * len(pages) / (len(links) + 1)) - 1, len(pages) - 1))
        document_index, page_index = pages[flattened_index]
        page = documents[document_index]["pages"][page_index]
        insertion = min(len(page), max(1, len(page) // 2))
        page[insertion:insertion] = [link]
        placements.append({"link_index": index, "document_index": document_index, "page_index": page_index, "word_index": insertion})
    return {
        "source": {"repository": f"https://github.com/{REPOSITORY}", "ref": args.ref, "path": DEFAULT_SOURCE},
        "task": "uuid_chain",
        "seed": args.seed,
        "chain_length": args.chain_length,
        "context_words": args.context_words,
        "page_words": args.page_words,
        "pages_per_document": args.pages_per_document,
        "chain": chain,
        "links": links,
        "placements": placements,
        "documents": [{"title": document["title"], "pages": [" ".join(page) for page in document["pages"]]}
                      for document in documents],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--ref", default=DEFAULT_REF)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--chain-length", type=int, default=5)
    parser.add_argument("--context-words", type=int, default=32000)
    parser.add_argument("--page-words", type=int, default=800)
    parser.add_argument("--pages-per-document", type=int, default=8)
    args = parser.parse_args()
    if args.chain_length < 2 or args.context_words < 1 or args.page_words < 1 or args.pages_per_document < 1:
        raise SystemExit("chain length must be >= 2 and sizes must be positive")
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(build_recipe(args), indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
