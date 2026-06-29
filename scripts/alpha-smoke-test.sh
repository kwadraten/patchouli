#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}/.."

echo "==> Restore"
dotnet restore LiteratureApp.sln
echo "==> Build"
dotnet build LiteratureApp.sln --no-restore
echo "==> Test"
dotnet test LiteratureApp.sln --no-build
echo "==> Vulnerability audit"
dotnet list LiteratureApp.sln package --vulnerable --include-transitive
echo "==> Alpha smoke check passed"
