#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
echo "==> RC restore"; dotnet restore LiteratureApp.sln
echo "==> RC build"; dotnet build LiteratureApp.sln --no-restore
echo "==> RC test"; dotnet test LiteratureApp.sln --no-build
echo "==> RC vulnerability audit"; dotnet list LiteratureApp.sln package --vulnerable --include-transitive
echo "==> MCP help"; dotnet run --project src/LiteratureApp.McpServer/LiteratureApp.McpServer.csproj -- --help >/dev/null
test -f .agent/PRD.md
test -f .agent/domain.md
test -f .agent/minimal-closed-loop-execution-plan.md
echo "==> Alpha RC smoke check passed"
