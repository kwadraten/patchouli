#!/usr/bin/env bash
# Linux/macOS equivalent of inspect-code.ps1.
# Runs JetBrains InspectCode (jb) with the same version pin, SARIF report and
# blocking-rule policy as the PowerShell version.
#
# Usage:
#   scripts/inspect-code.sh [--artifacts-path <.tmp/...>]
#                           [--caches-path <.tmp/...>]
#                           [--use-existing-build]
#
# Requires: dotnet SDK on PATH, jq, and the pinned jb global tool
# (dotnet tool install -g JetBrains.ReSharper.GlobalTools --version 2026.1.4).
# Exit code: jb's own exit code on tool failure; 1 when blocking issues are
# found in the report; 0 when the report is clean.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
expected_version='2026.1.4'
artifact_directory="$root/.tmp"
report_path="$artifact_directory/inspectcode.sarif"

artifacts_path='.tmp/build/inspection'
caches_path='.tmp/build/inspection-caches'
use_existing_build=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --artifacts-path)
            [[ $# -ge 2 ]] || { echo "--artifacts-path requires a value" >&2; exit 2; }
            artifacts_path="$2"
            shift 2
            ;;
        --caches-path)
            [[ $# -ge 2 ]] || { echo "--caches-path requires a value" >&2; exit 2; }
            caches_path="$2"
            shift 2
            ;;
        --use-existing-build)
            use_existing_build=1
            shift
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

require_inside_tmp() {
    # $1 = path (relative to root or absolute), $2 = label
    local resolved
    resolved="$(realpath -m -- "$root/$1")"
    if [[ "$1" = /* ]]; then
        resolved="$(realpath -m -- "$1")"
    fi
    case "$resolved" in
        "$root/.tmp/"*) printf '%s' "$resolved" ;;
        *)
            echo "$2 must be inside the repository .tmp directory: $resolved" >&2
            exit 1
            ;;
    esac
}

# The jb apphost does not search PATH for the runtime; when dotnet lives in a
# non-standard root (e.g. ~/.dotnet), point DOTNET_ROOT at it. When dotnet is
# not on PATH at all, fall back to the well-known install locations.
if [[ -z "${DOTNET_ROOT:-}" ]]; then
    dotnet_bin="$(command -v dotnet || true)"
    if [[ -z "$dotnet_bin" ]]; then
        for candidate in "$HOME/.dotnet/dotnet" /usr/share/dotnet/dotnet \
                         /usr/local/share/dotnet/dotnet /opt/dotnet/dotnet; do
            if [[ -x "$candidate" ]]; then
                dotnet_bin="$candidate"
                break
            fi
        done
    fi
    [[ -n "$dotnet_bin" ]] || { echo "dotnet not found on PATH or in well-known install locations" >&2; exit 1; }
    DOTNET_ROOT="$(dirname "$(readlink -f "$dotnet_bin")")"
    export DOTNET_ROOT
fi
export PATH="$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools"

command -v jq >/dev/null 2>&1 || { echo "jq is required to evaluate the InspectCode report" >&2; exit 1; }

sdk_version="$(dotnet --version | tr -d '[:space:]')"
msbuild_path="$DOTNET_ROOT/sdk/$sdk_version/MSBuild.dll"
[[ -f "$msbuild_path" ]] || {
    echo "Could not find the MSBuild.dll for .NET SDK $sdk_version at '$msbuild_path'." >&2
    exit 1
}

command -v jb >/dev/null 2>&1 || {
    echo "JetBrains jb tool not found. Install it with:" >&2
    echo "  dotnet tool install -g JetBrains.ReSharper.GlobalTools --version $expected_version" >&2
    exit 1
}
actual_version="$(jb inspectcode --version | sed -n 's/^Version:[[:space:]]*//p' | head -n 1 | tr -d '[:space:]')"
[[ "$actual_version" == "$expected_version" ]] || {
    echo "JetBrains Inspect Code $expected_version is required; found '$actual_version'." >&2
    exit 1
}

mkdir -p "$artifact_directory"
resolved_artifacts_path="$(require_inside_tmp "$artifacts_path" 'Inspection build outputs')"
resolved_caches_path="$(require_inside_tmp "$caches_path" 'Inspection caches')"

additional_arguments=( "--caches-home=$resolved_caches_path" )
if [[ "$use_existing_build" -eq 1 ]]; then
    # InspectCode cannot resolve F# project metadata reliably in SDK artifacts output. After a
    # normal build, use its resolved assemblies without triggering another solution build.
    additional_arguments+=( '--no-build' '--properties=PatchouliUseBuiltExtensionAssembly=true' )
else
    additional_arguments+=( "--properties=ArtifactsPath=$resolved_artifacts_path;UseArtifactsOutput=true" )
fi

set +e
jb inspectcode "$root/Patchouli.sln" \
    "--toolset-path=$msbuild_path" \
    "--output=$report_path" \
    --format=Sarif \
    --no-updates "${additional_arguments[@]}"
jb_exit=$?
set -e
[[ "$jb_exit" -eq 0 ]] || exit "$jb_exit"

blocking_rule_ids='[
  ".XAMLErrors",
  "Xaml.PossibleNullReferenceException",
  "AccessToDisposedClosure",
  "AccessToModifiedClosure",
  "AsyncVoidMethod",
  "AsyncVoidLambda",
  "EmptyGeneralCatchClause",
  "PossibleMultipleEnumeration",
  "ObjectDisposed"
]'

blocking_count="$(jq --argjson ids "$blocking_rule_ids" '
    [ .runs[]?.results[]?
      | select(
          .level == "error"
          or (.ruleId // "" | startswith("CSharpWarnings::"))
          or (.ruleId as $r | $ids | index($r) != null)
        )
    ] | length
' "$report_path")"

if [[ "$blocking_count" -eq 0 ]]; then
    echo "InspectCode passed. Report: $report_path"
    exit 0
fi

echo "InspectCode found $blocking_count blocking issue(s). Report: $report_path"
jq -r --argjson ids "$blocking_rule_ids" '
    .runs[]?.results[]?
    | select(
        .level == "error"
        or (.ruleId // "" | startswith("CSharpWarnings::"))
        or (.ruleId as $r | $ids | index($r) != null)
      )
    | "\(.ruleId): \(.locations[0].physicalLocation.artifactLocation.uri):\(.locations[0].physicalLocation.region.startLine): \(.message.text)"
' "$report_path"
exit 1
