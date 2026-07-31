# MCP A/B Agent Benchmark

Standard-library Python harness for comparing shell MCP-A with structured MCP-B through an OpenAI chat-completions-compatible model API, including DeepSeek. It contains neither credentials nor a library fixture. The checked-in manifest uses placeholders, so execution requires a provisioned, isolated Library snapshot and a local variables file.

## Safety and Preconditions

- The harness loads `.env` from this directory. Process environment values take precedence. `.env` is ignored by Git.
- `DEEPSEEK_API_KEY`, `MCP_A_SERVER_URL`, and `MCP_B_SERVER_URL` are required.
- Before every benchmark, preflight calls `initialize`, sends `notifications/initialized`, and calls `tools/list` on both endpoints.
- Tasks share their user prompt and scoring contract. `conditions.a` and `conditions.b` separately declare the tools and instruction available to each interface. A missing condition-specific tool hard-fails the run; it never emits an A-only comparison.
- Telemetry redacts configured secret values and common authorization field names. It records only aggregate timing, token usage, tool-call counts, errors, and response byte counts. It does not write prompts, model content, tool arguments/results, headers, or keys.
- MCP requests use JSON-RPC 2.0 POST. The server must accept the `2024-11-05` initialize protocol version.

## Setup

1. Copy `.env.example` to `.env` in this directory and set the API key, endpoint URLs, model name, and optional MCP bearer tokens.
2. Copy `variables.example.json` to a local untracked file, for example `variables.local.json`, and replace every `PROVISION_ME` value with values valid for the isolated benchmark Library. Do not use provider credentials as variable values.
3. Start MCP-A and MCP-B against independent copies of the same database snapshot. A exposes `patchouli_shell`; B exposes the applicable `patchouli.find`, `patchouli.fetch`, and `patchouli.cite` tools. `patchouli.put` remains intentionally unavailable until the atomic revision-gated write path is implemented.
4. Run 100 repetitions per task and condition with a fixed randomized schedule:

```powershell
python .agent/mcp-ab/benchmark/benchmark.py run --variables .agent/mcp-ab/benchmark/variables.local.json --runs 100
```

5. Render a result file as Markdown:

```powershell
python .agent/mcp-ab/benchmark/benchmark.py report --results .agent/mcp-ab/benchmark/artifacts/results-REPLACE.json --output .agent/mcp-ab/benchmark/artifacts/report.md
```

`attempt=1` is marked `cold`; later repetitions are marked `warm`. A valid cold/warm comparison also requires fresh server/library provisioning for the intended cache state.

## Manifest Contract

`manifest.schema.json` documents the version 2 manifest. A task has a stable `id`, optional phase, shared prompt and expected output, plus condition-specific tool allowlists and budgets. The harness exposes only the declared condition tools to the model and fails an unavailable-tool request.

Prompts and expected-output values can reference a variables-file key with `{{name}}`. Missing keys and `PROVISION_ME` values fail before the model call. `expected_output` supports required fields, JSON types, exact values, enum values, string patterns, and array minimum lengths. Score is the fraction of applicable checks passed.

## UUID Chain Task

`tasks.uuid-chain.json` adapts the `uuid_chain` task from [gkamradt/needle-in-a-haystack](https://github.com/gkamradt/needle-in-a-haystack). The benchmark pins the source recipe to commit `021385d68d3202e37893e9d3cd29011c569abe30` and uses the repository's `PaulGrahamEssays` text source. The upstream project is licensed under MIT; its `LICENSE.txt` remains the authoritative notice.

The preparation script downloads only the upstream text source into memory, creates a compact local recipe, and reproduces the upstream seeded UUID algorithm and even-spread placement idea. The recipe contains the source ref, chain, placements, and Patchouli-shaped documents, but it is a local artifact and must not be committed:

```powershell
python .agent/mcp-ab/benchmark/prepare_uuid_chain.py --output C:\path\uuid-chain.recipe.json --seed 1 --context-words 32000
dotnet src/Patchouli.McpServer/bin/Debug/net10.0/Patchouli.McpServer.dll --db C:\path\uuid-chain.sqlite --seed-uuid-chain-fixture --recipe C:\path\uuid-chain.recipe.json
```

The importer maps each source essay to an Item and primary DocumentInstance, splits it into Page-sized text, commits each page as a DocumentTreeRevision, and rebuilds SearchUnits plus the library FTS projection. UUID links are inserted into ordinary page text, so the haystack is exposed through the same text-only Library boundary as normal data. The seed command prints the variables needed by the task; place them in a local variables file using the shape in `variables.uuid-chain.example.json`.

The task question intentionally only asks `What is the value associated with <start>?`, matching the upstream task. A receives only `patchouli_shell`; B receives its structured surface. Both conditions allow repeated native tool calls with a high 64-call cap, which leaves ample room for exploration while preventing a malformed tool loop from consuming an unbounded run. In A, the task instruction uses the native evidence workflow: `rg --meta <uuid> /texts` locates an evidence URI and `evidence '<uri>'` reads the complete pinned page. The scorer preserves upstream partial-credit semantics by checking the furthest contiguous UUID hop and also requires the final UUID.

Run the task against isolated A/B copies of the same seeded database:

```powershell
python .agent/mcp-ab/benchmark/benchmark.py run --manifest .agent/mcp-ab/benchmark/tasks.uuid-chain.json --variables .agent/mcp-ab/benchmark/variables.uuid-chain.json --total-runs 100
```

## Files and Output

- `benchmark.py`: CLI, dotenv parsing, MCP JSON-RPC client, model loop, telemetry, scoring, and report renderer.
- `tasks.example.json`: placeholder task suite with distinct A/B tool declarations.
- `variables.example.json`: non-secret provisioning template.
- `artifacts/telemetry-<run-id>.jsonl`: safe aggregate telemetry.
- `artifacts/results-<run-id>.json`: scored results, including token, call, error, response-byte, and latency metrics.

Verify syntax without dependencies:

```powershell
python -m py_compile .agent/mcp-ab/benchmark/benchmark.py
```
