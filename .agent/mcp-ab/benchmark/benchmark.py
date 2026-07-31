#!/usr/bin/env python3
"""Model-neutral, OpenAI-compatible MCP A/B agent benchmark."""
import argparse
import json
import os
import random
import re
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parent
SECRET_NAMES = {"DEEPSEEK_API_KEY", "MCP_A_AUTH_TOKEN", "MCP_B_AUTH_TOKEN"}
PLACEHOLDER = re.compile(r"{{([A-Za-z_][A-Za-z0-9_]*)}}")


def load_dotenv(path):
    if not path.exists():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = (part.strip() for part in line.split("=", 1))
        if value[:1] in ("'", '"') and value[-1:] == value[:1]:
            value = value[1:-1]
        os.environ.setdefault(key, value)


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def progress(message):
    print("[benchmark] " + message, file=sys.stderr, flush=True)


def scrub(value, secrets):
    if isinstance(value, dict):
        return {key: "[REDACTED]" if key.lower() in {"authorization", "api_key", "token", "x-api-key"} else scrub(item, secrets) for key, item in value.items()}
    if isinstance(value, list):
        return [scrub(item, secrets) for item in value]
    if isinstance(value, str):
        for secret in secrets:
            if secret:
                value = value.replace(secret, "[REDACTED]")
    return value


class Telemetry:
    def __init__(self, path, secrets):
        self.path, self.secrets = path, secrets
        path.parent.mkdir(parents=True, exist_ok=True)

    def write(self, event, **fields):
        record = scrub({"timestamp": utc_now(), "event": event, **fields}, self.secrets)
        with self.path.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, ensure_ascii=True, sort_keys=True) + "\n")


def post_json(url, payload, timeout, token=None, expect_response=True):
    headers = {"Content-Type": "application/json", "Accept": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    request = Request(url, data=json.dumps(payload).encode("utf-8"), headers=headers, method="POST")
    try:
        with urlopen(request, timeout=timeout) as response:
            body = response.read()
            if not expect_response:
                return None
            return json.loads(body.decode("utf-8"))
    except HTTPError as error:
        raise RuntimeError("HTTP %s from remote service" % error.code) from error
    except (URLError, TimeoutError, json.JSONDecodeError) as error:
        raise RuntimeError("request to remote service failed") from error


class McpClient:
    def __init__(self, label, url, timeout, token, telemetry):
        self.label, self.url, self.timeout, self.token, self.telemetry = label, url, timeout, token, telemetry
        self.id = 0

    def call(self, method, params=None):
        self.id += 1
        request_id, started = self.id, time.monotonic()
        response = post_json(self.url, {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}}, self.timeout, self.token)
        elapsed_ms = round((time.monotonic() - started) * 1000)
        if response.get("id") != request_id or response.get("jsonrpc") != "2.0":
            raise RuntimeError("%s returned an invalid JSON-RPC response" % self.label)
        if "error" in response:
            raise RuntimeError("%s %s returned a JSON-RPC error" % (self.label, method))
        result = response.get("result", {})
        metrics = {"elapsed_ms": elapsed_ms, "response_bytes": len(json.dumps(result, ensure_ascii=True).encode("utf-8"))}
        self.telemetry.write("mcp_rpc", endpoint=self.label, method=method, **metrics)
        return result, metrics

    def notify_initialized(self):
        post_json(self.url, {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}}, self.timeout, self.token, expect_response=False)

    def initialize(self):
        self.call("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "mcp-ab-benchmark", "version": "2"}})
        self.notify_initialized()
        result, _ = self.call("tools/list")
        return {tool["name"]: tool for tool in result.get("tools", []) if isinstance(tool, dict) and isinstance(tool.get("name"), str)}


def load_json(path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError("cannot load %s: %s" % (path, error)) from error


def condition(task, endpoint):
    return task["conditions"][endpoint]


def validate_manifest(manifest):
    if not isinstance(manifest, dict) or manifest.get("version") != 2 or not isinstance(manifest.get("tasks"), list) or not manifest["tasks"]:
        raise ValueError("manifest must contain version 2 and a non-empty tasks array")
    ids = set()
    for task in manifest["tasks"]:
        if not isinstance(task, dict) or not isinstance(task.get("id"), str) or task["id"] in ids:
            raise ValueError("every task requires a unique string id")
        ids.add(task["id"])
        if not isinstance(task.get("prompt"), str) or not isinstance(task.get("conditions"), dict):
            raise ValueError("task %s requires prompt and conditions" % task["id"])
        for endpoint in ("a", "b"):
            value = task["conditions"].get(endpoint)
            if not isinstance(value, dict) or not isinstance(value.get("required_tools"), list) or not value["required_tools"]:
                raise ValueError("task %s requires non-empty conditions.%s.required_tools" % (task["id"], endpoint))
            if not all(isinstance(name, str) and name for name in value["required_tools"]):
                raise ValueError("task %s has an invalid tool name" % task["id"])
            if "allow_multiple_tool_rounds" in value and not isinstance(value["allow_multiple_tool_rounds"], bool):
                raise ValueError("task %s has an invalid allow_multiple_tool_rounds value" % task["id"])
            if "max_tool_calls" in value and value["max_tool_calls"] is not None and (
                    not isinstance(value["max_tool_calls"], int) or value["max_tool_calls"] < 1):
                raise ValueError("task %s has an invalid max_tool_calls value" % task["id"])
        output = task.get("expected_output")
        if not isinstance(output, dict) or not isinstance(output.get("required"), list) or not isinstance(output.get("properties"), dict):
            raise ValueError("task %s has invalid expected_output" % task["id"])


def substitute(value, variables):
    def replace(match):
        name = match.group(1)
        if name not in variables or variables[name] == "PROVISION_ME":
            raise ValueError("required variable %r is not provisioned" % name)
        return str(variables[name])
    return PLACEHOLDER.sub(replace, value)


def resolve_templates(value, variables):
    if isinstance(value, str):
        match = PLACEHOLDER.fullmatch(value)
        if match and match.group(1) in variables and variables[match.group(1)] != "PROVISION_ME":
            return variables[match.group(1)]
        return substitute(value, variables)
    if isinstance(value, list):
        return [resolve_templates(item, variables) for item in value]
    if isinstance(value, dict):
        return {key: resolve_templates(item, variables) for key, item in value.items()}
    return value


def tool_definitions(tools):
    return [{"type": "function", "function": {"name": model_tool_name(name), "description": tool.get("description", ""), "parameters": tool.get("inputSchema", {"type": "object"})}} for name, tool in tools.items()]


def model_tool_name(mcp_name):
    return mcp_name.replace(".", "__")


def mcp_tool_name(model_name, selected):
    return next((name for name in selected if model_tool_name(name) == model_name), None)


def model_request(base_url, api_key, model, messages, tools, timeout):
    payload = {"model": model, "messages": messages, "temperature": 0, "response_format": {"type": "json_object"}}
    if tools:
        payload["tools"] = tools
        payload["tool_choice"] = "auto"
    response = post_json(base_url.rstrip("/") + "/chat/completions", payload, timeout, api_key)
    try:
        return response["choices"][0]["message"], response.get("usage", {})
    except (KeyError, IndexError, TypeError) as error:
        raise RuntimeError("model response did not contain choices[0].message") from error


def parse_output(text):
    if isinstance(text, str) and text.startswith("```") and text.endswith("```"):
        text = text.split("\n", 1)[1] if "\n" in text else text
        text = text.rsplit("```", 1)[0].strip()
    try:
        return json.loads(text)
    except (TypeError, json.JSONDecodeError):
        if not isinstance(text, str):
            return None
        decoder = json.JSONDecoder()
        for index, character in enumerate(text):
            if character != "{":
                continue
            try:
                value, _ = decoder.raw_decode(text[index:])
                return value if isinstance(value, dict) else None
            except json.JSONDecodeError:
                continue
        return None


def matches_type(value, name):
    return {"string": isinstance(value, str), "number": isinstance(value, (int, float)) and not isinstance(value, bool), "integer": isinstance(value, int) and not isinstance(value, bool), "boolean": isinstance(value, bool), "array": isinstance(value, list), "object": isinstance(value, dict)}.get(name, False)


def score(output, expected):
    if not isinstance(output, dict):
        return 0.0, [{"field": "$", "passed": False, "reason": "final response is not a JSON object"}]
    checks = [{"field": field, "passed": field in output, "reason": "missing" if field not in output else ""} for field in expected["required"]]
    for field, rule in expected["properties"].items():
        if field not in output:
            continue
        value, passed, reason = output[field], matches_type(output[field], rule["type"]), "type"
        if passed and "equals" in rule:
            passed, reason = value == rule["equals"], "equals"
        if passed and "enum" in rule:
            passed, reason = value in rule["enum"], "enum"
        if passed and "pattern" in rule:
            passed, reason = isinstance(value, str) and re.search(rule["pattern"], value) is not None, "pattern"
        if passed and "min_items" in rule:
            passed, reason = isinstance(value, list) and len(value) >= rule["min_items"], "min_items"
        if passed and "all_pattern" in rule:
            passed, reason = isinstance(value, list) and all(
                isinstance(item, str) and re.search(rule["all_pattern"], item) is not None for item in value), "all_pattern"
        if passed and rule.get("unique_items"):
            passed, reason = isinstance(value, list) and len({json.dumps(item, sort_keys=True) for item in value}) == len(value), "unique_items"
        if passed and "chain_match" in rule:
            chain = rule["chain_match"]
            if isinstance(chain, str):
                try:
                    chain = json.loads(chain)
                except json.JSONDecodeError:
                    chain = []
            passed, reason = isinstance(value, list) and isinstance(chain, list) and all(
                isinstance(item, str) and item in chain for item in value), "chain_match"
        checks.append({"field": field, "passed": passed, "reason": "" if passed else reason})
    chain_rules = [rule for rule in expected["properties"].values() if "chain_match" in rule]
    chain_score = 0.0
    if chain_rules and isinstance(output.get("found"), list):
        chain = chain_rules[0]["chain_match"]
        if isinstance(chain, str):
            try:
                chain = json.loads(chain)
            except json.JSONDecodeError:
                chain = []
        found = [item for item in chain if item in output["found"]] if isinstance(chain, list) else []
        furthest = -1
        for index, item in enumerate(chain if isinstance(chain, list) else []):
            if item not in output["found"]:
                break
            furthest = index
        chain_score = (furthest + 1) / len(chain) if chain else 0.0
        checks.append({"field": "found.chain_progress", "passed": chain_score == 1.0, "score": chain_score})
    base_score = sum(check["passed"] for check in checks if "score" not in check) / max(1, sum("score" not in check for check in checks))
    return ((base_score + chain_score) / 2 if chain_rules else base_score), checks


def preflight(manifest, telemetry):
    missing = [name for name in ("DEEPSEEK_API_KEY", "MCP_A_SERVER_URL", "MCP_B_SERVER_URL") if not os.environ.get(name)]
    if missing:
        raise RuntimeError("missing required environment variables: " + ", ".join(missing))
    if not os.environ["DEEPSEEK_API_KEY"].strip():
        raise RuntimeError("DEEPSEEK_API_KEY must not be blank")
    timeout, clients = float(os.environ.get("MCP_TIMEOUT_SECONDS", "180")), {}
    for endpoint in ("a", "b"):
        progress("preflight MCP-%s" % endpoint.upper())
        client = McpClient(endpoint.upper(), os.environ["MCP_%s_SERVER_URL" % endpoint.upper()], timeout, os.environ.get("MCP_%s_AUTH_TOKEN" % endpoint.upper()), telemetry)
        tools = client.initialize()
        required = {name for task in manifest["tasks"] for name in condition(task, endpoint)["required_tools"]}
        absent = sorted(required - set(tools))
        if absent:
            raise RuntimeError("MCP-%s preflight failed; required tools unavailable: %s" % (endpoint.upper(), ", ".join(absent)))
        clients[endpoint] = (client, tools)
        telemetry.write("preflight_endpoint", endpoint=endpoint.upper(), available_tools=sorted(tools), required_tools=sorted(required))
        progress("MCP-%s ready (%d required tools)" % (endpoint.upper(), len(required)))
    return clients


def execute_task(client, endpoint, available_tools, task, variables, run_id, attempt, timeout, model, base_url, api_key):
    config, prompt = condition(task, endpoint), substitute(task["prompt"], variables)
    expected = resolve_templates(task["expected_output"], variables)
    selected = {name: available_tools[name] for name in config["required_tools"]}
    system = config.get("system", "Use only the tools supplied for this condition. Do not claim access to unavailable resources.")
    messages = [{"role": "system", "content": system + " Final answer must be only a JSON object matching this schema: " + json.dumps(expected)}, {"role": "user", "content": prompt}]
    tool_calls, tool_errors, response_bytes, model_turns, input_tokens, output_tokens = 0, 0, 0, 0, 0, 0
    used_tools, started = set(), time.monotonic()
    limit = config.get("max_tool_calls", task.get("max_tool_calls", 12))
    multiple_rounds = config.get("allow_multiple_tool_rounds", False)
    try:
        while True:
            tools = tool_definitions(selected) if multiple_rounds or model_turns == 0 else None
            message, usage = model_request(base_url, api_key, model, messages, tools, timeout)
            model_turns += 1
            input_tokens += int(usage.get("prompt_tokens", 0) or 0)
            output_tokens += int(usage.get("completion_tokens", 0) or 0)
            messages.append(message)
            calls = message.get("tool_calls") or []
            if not calls:
                break
            for call in calls:
                if limit is not None and tool_calls >= limit:
                    raise RuntimeError("task exceeded max_tool_calls")
                tool_calls += 1
                model_name = call.get("function", {}).get("name")
                name = mcp_tool_name(model_name, selected)
                if name is None:
                    raise RuntimeError("model requested unavailable tool")
                try:
                    arguments = json.loads(call["function"].get("arguments", "{}"))
                except json.JSONDecodeError as error:
                    raise RuntimeError("model emitted invalid tool arguments") from error
                result, metrics = client.call("tools/call", {"name": name, "arguments": arguments})
                used_tools.add(name)
                response_bytes += metrics["response_bytes"]
                tool_errors += int(bool(result.get("isError")))
                messages.append({"role": "tool", "tool_call_id": call["id"], "content": json.dumps(result, ensure_ascii=True)})
            if not multiple_rounds:
                messages.append({"role": "system", "content": "Use the returned tool result. Do not call another tool. Return only the required JSON object now."})
        output = parse_output(message.get("content"))
        task_score, checks = score(output, expected)
        failure = None
    except RuntimeError as error:
        task_score, checks, failure = 0.0, [{"field": "$", "passed": False, "reason": str(error)}], "runtime_error"
    return {"run_id": run_id, "attempt": attempt, "endpoint": endpoint.upper(), "task_id": task["id"], "phase": task.get("phase", "I"), "temperature_state": "cold" if attempt == 1 else "warm", "score": task_score, "checks": checks, "failure": failure, "elapsed_ms": round((time.monotonic() - started) * 1000), "tool_calls": tool_calls, "tool_errors": tool_errors, "tool_response_bytes": response_bytes, "distinct_tools": len(used_tools), "model_turns": model_turns, "input_tokens": input_tokens, "output_tokens": output_tokens}


def run(args):
    load_dotenv(ROOT / ".env")
    manifest, variables = load_json(args.manifest), load_json(args.variables)
    validate_manifest(manifest)
    if not isinstance(variables, dict):
        raise ValueError("variables must be a JSON object")
    if args.runs < 1:
        raise ValueError("--runs must be at least 1")
    secrets, run_id = [os.environ.get(name, "") for name in SECRET_NAMES], str(uuid.uuid4())
    telemetry = Telemetry(Path(args.output) / ("telemetry-%s.jsonl" % run_id), secrets)
    telemetry.write("run_started", run_id=run_id, manifest=str(args.manifest), runs=args.runs, total_runs=args.total_runs)
    progress("run %s starting" % run_id)
    clients = preflight(manifest, telemetry)
    timeout = float(os.environ.get("MCP_TIMEOUT_SECONDS", "180"))
    model, base_url, api_key = os.environ.get("DEEPSEEK_MODEL", "deepseek-chat"), os.environ.get("MODEL_BASE_URL", "https://api.deepseek.com"), os.environ["DEEPSEEK_API_KEY"]
    combinations = [(task, endpoint) for task in manifest["tasks"] for endpoint in ("a", "b")]
    if args.total_runs is None:
        schedule = [(attempt, task, endpoint) for attempt in range(1, args.runs + 1) for task, endpoint in combinations]
    else:
        if args.total_runs < len(combinations):
            raise ValueError("--total-runs must cover every task and condition at least once")
        schedule = [(1, task, endpoint) for task, endpoint in combinations]
        for position in range(len(combinations), args.total_runs):
            task, endpoint = combinations[position % len(combinations)]
            attempt = position // len(combinations) + 1
            schedule.append((attempt, task, endpoint))
        progress("scheduled %d total runs across %d task/condition combinations" % (len(schedule), len(combinations)))
    random.Random(args.seed).shuffle(schedule)
    results = []
    total = len(schedule)
    for position, (attempt, task, endpoint) in enumerate(schedule, start=1):
        progress("%d/%d MCP-%s %s attempt=%d" % (position, total, endpoint.upper(), task["id"], attempt))
        client, tools = clients[endpoint]
        record = execute_task(client, endpoint, tools, task, variables, run_id, attempt, timeout, model, base_url, api_key)
        results.append(record)
        telemetry.write("task_completed", **record)
        progress("%d/%d completed score=%.3f calls=%d errors=%d elapsed_ms=%d" % (position, total, record["score"], record["tool_calls"], record["tool_errors"], record["elapsed_ms"]))
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    result_path = output / ("results-%s.json" % run_id)
    result_path.write_text(json.dumps({"run_id": run_id, "created_at": utc_now(), "model": model, "runs_per_task_condition": args.runs if args.total_runs is None else None, "requested_total_runs": args.total_runs, "results": results}, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    telemetry.write("run_completed", run_id=run_id, result_file=str(result_path), task_count=len(results))
    progress("run complete: %d results written" % len(results))
    print(result_path)


def mean(rows, field):
    return sum(row[field] for row in rows) / len(rows) if rows else 0


def report(args):
    data, results = load_json(args.results), load_json(args.results).get("results", [])
    groups = {}
    for row in results:
        groups.setdefault((row["endpoint"], row.get("phase", "I"), row.get("temperature_state", "cold")), []).append(row)
    lines = ["# MCP A/B Benchmark Report", "", "Run: `%s`" % data.get("run_id", "unknown"), "", "| Endpoint | Phase | State | Tasks | Mean score | Pass rate | Input tokens | Output tokens | Tool calls | Tool errors | Response bytes | Latency (ms) |", "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for key, rows in sorted(groups.items()):
        lines.append("| %s | %s | %s | %d | %.3f | %.1f%% | %.0f | %.0f | %.1f | %.1f | %.0f | %.0f |" % (*key, len(rows), mean(rows, "score"), mean(rows, "score") * 100, mean(rows, "input_tokens"), mean(rows, "output_tokens"), mean(rows, "tool_calls"), mean(rows, "tool_errors"), mean(rows, "tool_response_bytes"), mean(rows, "elapsed_ms")))
    lines += ["", "## Per Task", "", "| Endpoint | Phase | State | Task | Attempt | Score | Calls | Errors | Bytes | Input | Output | Latency (ms) |", "| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    lines += ["| %s | %s | %s | %s | %d | %.3f | %d | %d | %d | %d | %d | %d |" % (row["endpoint"], row.get("phase", "I"), row.get("temperature_state", "cold"), row["task_id"], row["attempt"], row["score"], row["tool_calls"], row["tool_errors"], row["tool_response_bytes"], row["input_tokens"], row["output_tokens"], row["elapsed_ms"]) for row in results]
    Path(args.output).write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(args.output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    run_parser = subparsers.add_parser("run")
    run_parser.add_argument("--manifest", default=str(ROOT / "tasks.example.json"))
    run_parser.add_argument("--variables", required=True)
    run_parser.add_argument("--output", default=str(ROOT / "artifacts"))
    run_parser.add_argument("--runs", type=int, default=1, help="repetitions for every task and condition")
    run_parser.add_argument("--total-runs", type=int, help="total task-condition runs, balanced across the suite")
    run_parser.add_argument("--seed", type=int, default=20260731, help="deterministic schedule seed")
    report_parser = subparsers.add_parser("report")
    report_parser.add_argument("--results", required=True)
    report_parser.add_argument("--output", required=True)
    args = parser.parse_args()
    try:
        if args.command == "run":
            run(args)
        else:
            report(args)
    except (ValueError, RuntimeError) as error:
        print("error: " + str(error), file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
