#!/usr/bin/env bash
# Linux/macOS equivalent of cleanup-code.ps1.
# Runs JetBrains CleanupCode (jb) with the same profile, version pin and
# path constraints as the PowerShell version.
#
# Usage:
#   scripts/cleanup-code.sh [--include <glob[;glob...]>]...
#                           [--artifacts-path <.tmp/...>]
#                           [--caches-path <.tmp/...>]
#                           [--use-existing-build]
#
# Requires: dotnet SDK on PATH and the pinned jb global tool
# (dotnet tool install -g JetBrains.ReSharper.GlobalTools --version 2026.1.4).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
expected_version='2026.1.4'
profile='Built-in: Reformat & Apply Syntax Style'

include_default='src/**/*.cs;tests/**/*.cs;src/**/*.axaml;tests/**/*.axaml;src/**/*.fs;tests/**/*.fs;src/**/*.fsx'
include=''
artifacts_path='.tmp/build/cleanup'
caches_path='.tmp/build/cleanup-caches'
use_existing_build=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --include)
            [[ $# -ge 2 ]] || { echo "--include requires a value" >&2; exit 2; }
            include="${include:+$include;}$2"
            shift 2
            ;;
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
[[ -n "$include" ]] || include="$include_default"

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
actual_version="$(jb cleanupcode --version | sed -n 's/^Version:[[:space:]]*//p' | head -n 1 | tr -d '[:space:]')"
[[ "$actual_version" == "$expected_version" ]] || {
    echo "JetBrains Cleanup Code $expected_version is required; found '$actual_version'." >&2
    exit 1
}

resolved_caches_path="$(require_inside_tmp "$caches_path" 'Cleanup caches')"

additional_arguments=( "--caches-home=$resolved_caches_path" )
if [[ "$use_existing_build" -eq 1 ]]; then
    # Reuse normally built F# metadata; SDK artifacts output can leave InspectCode/CleanupCode
    # unable to resolve cross-language references. The caller must build before using this mode.
    additional_arguments+=( '--no-build' '--properties=PatchouliUseBuiltExtensionAssembly=true' )
elif [[ -n "$artifacts_path" ]]; then
    resolved_artifacts_path="$(require_inside_tmp "$artifacts_path" 'Cleanup build outputs')"
    additional_arguments+=( "--properties=ArtifactsPath=$resolved_artifacts_path;UseArtifactsOutput=true" )
fi

jb cleanupcode "$root/Patchouli.sln" \
    "--profile=$profile" \
    "--include=$include" \
    "--toolset-path=$msbuild_path" \
    --no-updates "${additional_arguments[@]}"
