#!/usr/bin/env python3
"""Summarize a test run into a normalized case-level JSON manifest.

Parses TRX (VSTest) and JUnit-style XML result files plus optional resource
logs, and emits per-case results with full test names (Theory parameters and
duplicate rows preserved as a multiset — never deduplicated by method name).

Case identity key: "<project>::<full test name incl. parameters>". Comparisons
against an expected list are multiset comparisons on that key.

Usage:
    summarize-test-run.py --trx results.trx [--project NAME]
                          [--expected expected-cases.txt]
                          [--log resource.log ...]
                          [--out summary.json] [--truncate-output N]

Expected file format: one case key per line ("project::full.test.name" or a
bare "full.test.name"); blank lines and '#' comments are ignored.

Exit codes:
    0  parsed OK, >0 results, expected multiset matched (when provided)
    1  zero results, or expected multiset mismatch
    2  input/parse error
"""

import argparse
import collections
import datetime as dt
import hashlib
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

TRX_NS_HINT = "TeamTest"
DEFAULT_TRUNCATE = 4000


def localname(tag):
    return tag.rsplit("}", 1)[-1]


def children(elem, name):
    return [c for c in elem if localname(c.tag) == name]


def child(elem, name):
    for c in elem:
        if localname(c.tag) == name:
            return c
    return None


def parse_iso8601(value):
    if not value:
        return None
    try:
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def iso_or_none(value):
    parsed = parse_iso8601(value)
    return parsed.isoformat() if parsed else (value or None)


def parse_duration(value):
    """Parse VSTest durations like '0:00:01.2345678' or '1.02:03:04.5' -> seconds."""
    if not value:
        return None
    try:
        return float(value)
    except ValueError:
        pass
    m = re.match(r"^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$", value)
    if m:
        days, hours, minutes, seconds = m.groups()
        return (
            int(days or 0) * 86400
            + int(hours) * 3600
            + int(minutes) * 60
            + float(seconds)
        )
    m = re.match(r"^(\d+):(\d+(?:\.\d+)?)$", value)
    if m:
        return int(m.group(1)) * 60 + float(m.group(2))
    return None


def text_of(elem):
    return elem.text if elem is not None and elem.text else None


def truncate(value, limit):
    if value is None or limit <= 0 or len(value) <= limit:
        return value
    return value[:limit] + f"...[truncated {len(value) - limit} chars]"


def file_sha256(path, limit=None):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        remaining = limit
        while True:
            chunk = fh.read(65536 if remaining is None else min(65536, remaining))
            if not chunk:
                break
            h.update(chunk)
            if remaining is not None:
                remaining -= len(chunk)
                if remaining <= 0:
                    break
    return h.hexdigest()


def make_case_key(project, test_name):
    return f"{project}::{test_name}" if project else test_name


def parse_trx(path, project, truncate_output):
    tree = ET.parse(path)
    root = tree.getroot()
    if localname(root.tag) != "TestRun":
        raise ValueError(f"{path}: not a TRX TestRun document")

    run = {
        "format": "vstest-trx",
        "id": root.get("id"),
        "name": root.get("name"),
        "run_user": root.get("runUser"),
        "times": {},
        "outcome": None,
    }
    times = child(root, "Times")
    if times is not None:
        run["times"] = {k: iso_or_none(v) for k, v in times.attrib.items()}
        start = parse_iso8601(times.get("start"))
        finish = parse_iso8601(times.get("finish"))
        if start and finish:
            run["wall_s"] = round((finish - start).total_seconds(), 3)

    counters = {}
    warnings = []
    summary = child(root, "ResultSummary")
    if summary is not None:
        run["outcome"] = summary.get("outcome")
        counters_elem = child(summary, "Counters")
        if counters_elem is not None:
            counters = {
                k: int(v) for k, v in counters_elem.attrib.items() if v.isdigit()
            }
        output_elem = child(summary, "Output")
        msg_elem = child(output_elem if output_elem is not None else summary, "StdOut")
        if text_of(msg_elem):
            run["stdout"] = text_of(msg_elem)
        run_infos = child(summary, "RunInfos")
        if run_infos is not None:
            for info in children(run_infos, "RunInfo"):
                text = text_of(child(info, "Text")) or info.get("outcome") or ""
                warnings.append(text.strip())

    # executionId -> definition, for class/method metadata.
    definitions = {}
    test_defs = child(root, "TestDefinitions")
    if test_defs is not None:
        for unit in test_defs.iter():
            if localname(unit.tag) != "UnitTest":
                continue
            method = child(unit, "TestMethod")
            exec_elem = child(unit, "Execution")
            for execution in unit.iter():
                if localname(execution.tag) != "Execution":
                    continue
                definitions[execution.get("id")] = {
                    "test_id": unit.get("id"),
                    "method": method.get("name") if method is not None else None,
                    "class_name": (
                        method.get("className") if method is not None else None
                    ),
                    "code_base": (
                        method.get("codeBase") if method is not None else None
                    ),
                }
            if exec_elem is None and unit.get("id"):
                definitions.setdefault(
                    unit.get("id"),
                    {
                        "test_id": unit.get("id"),
                        "method": (
                            method.get("name") if method is not None else None
                        ),
                        "class_name": (
                            method.get("className")
                            if method is not None
                            else None
                        ),
                        "code_base": (
                            method.get("codeBase")
                            if method is not None
                            else None
                        ),
                    },
                )

    cases = []

    def emit_result(result, parent_execution_id=None):
        execution_id = result.get("executionId")
        definition = definitions.get(execution_id) or definitions.get(
            result.get("testId")
        ) or {}
        output = child(result, "Output")
        error_info = child(output, "ErrorInfo") if output is not None else None
        error = None
        if error_info is not None:
            error = {
                "message": truncate(text_of(child(error_info, "Message")), truncate_output),
                "stack_trace": truncate(
                    text_of(child(error_info, "StackTrace")), truncate_output
                ),
            }
        stdout = stderr = None
        if output is not None:
            stdout = truncate(text_of(child(output, "StdOut")), truncate_output)
            stderr = truncate(text_of(child(output, "StdErr")), truncate_output)
        test_name = result.get("testName") or definition.get("method") or ""
        cases.append(
            {
                "key": make_case_key(project, test_name),
                "project": project,
                "test_name": test_name,
                "method": definition.get("method"),
                "class_name": definition.get("class_name"),
                "test_id": result.get("testId") or definition.get("test_id"),
                "execution_id": execution_id,
                "parent_execution_id": parent_execution_id,
                "outcome": result.get("outcome"),
                "duration_s": parse_duration(result.get("duration")),
                "start": iso_or_none(result.get("startTime")),
                "end": iso_or_none(result.get("endTime")),
                "error": error,
                "stdout": stdout,
                "stderr": stderr,
            }
        )
        inner = child(result, "InnerResults")
        if inner is not None:
            for nested in children(inner, "UnitTestResult"):
                emit_result(nested, parent_execution_id=execution_id)

    results_elem = child(root, "Results")
    if results_elem is not None:
        for result in children(results_elem, "UnitTestResult"):
            emit_result(result)

    return run, counters, warnings, cases


def parse_junit(path, project, truncate_output):
    tree = ET.parse(path)
    root = tree.getroot()
    if localname(root.tag) not in ("testsuites", "testsuite"):
        raise ValueError(f"{path}: not a JUnit testsuite document")

    suites = [root] if localname(root.tag) == "testsuite" else [
        s for s in root.iter() if localname(s.tag) == "testsuite"
    ]
    cases = []
    totals = {"tests": 0, "failures": 0, "errors": 0, "skipped": 0}
    for suite in suites:
        for key in totals:
            if suite.get(key) and suite.get(key).isdigit():
                totals[key] += int(suite.get(key))
        for tc in suite.iter():
            if localname(tc.tag) != "testcase":
                continue
            classname = tc.get("classname") or suite.get("name") or ""
            name = tc.get("name") or ""
            test_name = f"{classname}.{name}" if classname else name
            outcome = "Passed"
            error = None
            for sub in tc:
                tag = localname(sub.tag)
                if tag == "failure":
                    outcome = "Failed"
                    error = {"message": truncate(sub.get("message") or sub.text, truncate_output)}
                elif tag == "error":
                    outcome = "Error"
                    error = {"message": truncate(sub.get("message") or sub.text, truncate_output)}
                elif tag == "skipped":
                    outcome = "NotExecuted"
            cases.append(
                {
                    "key": make_case_key(project, test_name),
                    "project": project,
                    "test_name": test_name,
                    "method": name,
                    "class_name": classname or None,
                    "test_id": None,
                    "execution_id": None,
                    "parent_execution_id": None,
                    "outcome": outcome,
                    "duration_s": parse_duration(tc.get("time")),
                    "start": None,
                    "end": None,
                    "error": error,
                    "stdout": None,
                    "stderr": None,
                }
            )
    run = {
        "format": "junit-xml",
        "id": None,
        "name": root.get("name"),
        "run_user": None,
        "times": {},
        "outcome": None,
    }
    counters = {
        "total": totals["tests"] or len(cases),
        "executed": len(cases),
        "failed": totals["failures"] + totals["errors"],
        "notExecuted": totals["skipped"],
    }
    return run, counters, [], cases


def detect_and_parse(path, project, truncate_output):
    for _event, elem in ET.iterparse(path, events=("start",)):
        tag = localname(elem.tag)
        if tag == "TestRun":
            return parse_trx(path, project, truncate_output)
        if tag in ("testsuites", "testsuite"):
            return parse_junit(path, project, truncate_output)
        break
    raise ValueError(f"{path}: unrecognized results format")


def load_expected(path):
    expected = []
    with open(path, "r", encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if line and not line.startswith("#"):
                expected.append(line)
    return expected


def compare_multiset(expected, actual_keys):
    """Multiset diff. Expected entries with a 'project::' prefix match full
    keys; entries without a prefix match the test_name tail of actual keys.
    Each actual case is claimed at most once, so duplicates stay visible."""
    exp_full = collections.Counter(e for e in expected if "::" in e)
    exp_tail = collections.Counter(e for e in expected if "::" not in e)
    act_full = collections.Counter(actual_keys)

    missing = []
    for key, count in exp_full.items():
        missing.extend([key] * max(0, count - act_full.get(key, 0)))

    consumed = collections.Counter()
    for key, count in exp_full.items():
        consumed[key] = min(count, act_full.get(key, 0))

    for key, count in exp_tail.items():
        remaining = count
        for actual_key, actual_count in act_full.items():
            if actual_key.split("::", 1)[-1] != key:
                continue
            claimed = min(remaining, actual_count - consumed[actual_key])
            consumed[actual_key] += claimed
            remaining -= claimed
            if remaining <= 0:
                break
        if remaining > 0:
            missing.extend([key] * remaining)

    extra = []
    for key, count in act_full.items():
        extra.extend([key] * max(0, count - consumed[key]))
    return missing, extra


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trx", dest="results", required=True,
                        help="Path to a TRX or JUnit XML results file")
    parser.add_argument("--project", default="",
                        help="Project name used in case keys (project::test.name)")
    parser.add_argument("--expected", default=None,
                        help="Optional file listing the expected case multiset")
    parser.add_argument("--log", dest="logs", action="append", default=[],
                        help="Optional resource/phase log files to fingerprint into the manifest")
    parser.add_argument("--out", default=None,
                        help="Write the full manifest JSON here")
    parser.add_argument("--truncate-output", type=int, default=DEFAULT_TRUNCATE,
                        help="Max chars kept per stdout/stderr/error field (0 = no truncation)")
    parser.add_argument("--quiet", action="store_true",
                        help="Do not print the compact stdout summary")
    args = parser.parse_args(argv)

    path = args.results
    if not os.path.exists(path):
        print(f"error: results file not found: {path}", file=sys.stderr)
        return 2
    if os.path.getsize(path) == 0:
        print(f"error: results file is empty: {path}", file=sys.stderr)
        return 2

    try:
        run, counters, warnings, cases = detect_and_parse(
            path, args.project or None, args.truncate_output
        )
    except (ET.ParseError, ValueError) as exc:
        print(f"error: failed to parse {path}: {exc}", file=sys.stderr)
        return 2

    outcome_counts = collections.Counter(c.get("outcome") or "Unknown" for c in cases)
    counts = {
        "results": len(cases),
        "distinct_cases": len({c["key"] for c in cases}),
        "passed": outcome_counts.get("Passed", 0),
        "failed": outcome_counts.get("Failed", 0) + outcome_counts.get("Error", 0),
        "skipped": outcome_counts.get("NotExecuted", 0),
        "other": sum(
            v
            for k, v in outcome_counts.items()
            if k not in ("Passed", "Failed", "Error", "NotExecuted")
        ),
        "by_outcome": dict(outcome_counts),
    }

    incomplete_signals = []
    outcome = run.get("outcome")
    if outcome and outcome not in ("Completed", "Failed"):
        incomplete_signals.append(f"run outcome is '{outcome}'")
    total = counters.get("total")
    executed = counters.get("executed")
    # xUnit's skipped rows have outcome NotExecuted but are not included in the
    # executed counter (and some adapters leave counters.notExecuted at zero).
    # Count the actual rows; the runner separately enforces its skip allowlist.
    if total is not None and len(cases) < total:
        incomplete_signals.append(
            f"results contain {len(cases)}/{total} declared cases - run truncated"
        )
    elif total is not None and executed is not None and executed + counts["skipped"] < total:
        incomplete_signals.append(
            f"counters show {executed} executed and {counts['skipped']} skipped of {total} - run truncated"
        )
    for w in warnings:
        # RunInfo can also contain adapter logs with test method names. Words
        # such as 'cancellation' in a passing case do not mean the run aborted.
        if re.search(
            r"\b(?:test run|test execution|test host|testhost(?:\.exe)?|test session)\s+"
            r"(?:(?:was|has been|is)\s+)?(?:aborted|cancelled|canceled|terminated|timed out)\b",
            w or "", re.I
        ):
            incomplete_signals.append(f"run warning: {w}")

    expected_report = None
    expected_mismatch = False
    if args.expected:
        if not os.path.exists(args.expected):
            print(f"error: expected file not found: {args.expected}", file=sys.stderr)
            return 2
        expected_list = load_expected(args.expected)
        missing, extra = compare_multiset(expected_list, [c["key"] for c in cases])
        expected_report = {
            "file": args.expected,
            "expected_count": len(expected_list),
            "missing_count": len(missing),
            "extra_count": len(extra),
            "missing": missing,
            "extra": extra,
        }
        expected_mismatch = bool(missing or extra)

    log_entries = []
    for log_path in args.logs:
        entry = {"path": log_path}
        if os.path.exists(log_path):
            entry["size_bytes"] = os.path.getsize(log_path)
            entry["sha256"] = file_sha256(log_path)
        else:
            entry["missing"] = True
        log_entries.append(entry)

    manifest = {
        "source": {
            "path": os.path.abspath(path),
            "format": run["format"],
            "size_bytes": os.path.getsize(path),
            "sha256": file_sha256(path),
        },
        "project": args.project or None,
        "run": run,
        "counters": counters,
        "counts": counts,
        "run_warnings": warnings,
        "incomplete_signals": incomplete_signals,
        "expected": expected_report,
        "logs": log_entries,
        "cases": cases,
        "generated_at": dt.datetime.now(dt.timezone.utc).isoformat(),
    }

    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as fh:
            json.dump(manifest, fh, indent=2, ensure_ascii=False)

    compact = {
        "status": "ok",
        "outcome": outcome,
        "results": counts["results"],
        "passed": counts["passed"],
        "failed": counts["failed"],
        "skipped": counts["skipped"],
        "incomplete_signals": incomplete_signals,
        "expected": (
            {
                "missing_count": expected_report["missing_count"],
                "extra_count": expected_report["extra_count"],
            }
            if expected_report
            else None
        ),
    }

    if counts["results"] == 0:
        compact["status"] = "empty-results"
        expected_mismatch = True
    elif expected_mismatch:
        compact["status"] = "case-mismatch"
    elif incomplete_signals:
        compact["status"] = "incomplete"

    if not args.quiet:
        print(json.dumps(compact, ensure_ascii=False))

    return 1 if expected_mismatch else 0


if __name__ == "__main__":
    sys.exit(main())
