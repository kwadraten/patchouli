#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
echo "==> RC restore"; dotnet restore LiteratureApp.sln
echo "==> RC build"; dotnet build LiteratureApp.sln --no-restore
echo "==> RC test"; dotnet test LiteratureApp.sln --no-build
echo "==> RC vulnerability audit"; dotnet list LiteratureApp.sln package --vulnerable --include-transitive
echo "==> MCP help"; dotnet run --project src/LiteratureApp.McpServer/LiteratureApp.McpServer.csproj -- --help >/dev/null
test -f docs/ALPHA_RC_CHECKLIST.md
test -f docs/ALPHA_DATA_SAFETY_AUDIT.md
test -f docs/ALPHA_RELEASE_NOTES.md
echo "==> Alpha RC smoke check passed"
