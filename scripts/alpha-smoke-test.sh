#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}/.."

echo "==> Restore"
dotnet restore Patchouli.sln
echo "==> Build"
dotnet build Patchouli.sln --no-restore
echo "==> Test"
dotnet test Patchouli.sln --no-build
echo "==> Vulnerability audit"
dotnet list Patchouli.sln package --vulnerable --include-transitive
echo "==> Alpha smoke check passed"
