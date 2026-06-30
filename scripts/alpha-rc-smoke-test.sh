#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
echo "==> RC restore"; dotnet restore Patchouli.sln
echo "==> RC build"; dotnet build Patchouli.sln --no-restore
echo "==> RC test"; dotnet test Patchouli.sln --no-build
echo "==> RC vulnerability audit"; dotnet list Patchouli.sln package --vulnerable --include-transitive
echo "==> MCP help"; dotnet run --project src/Patchouli.McpServer/Patchouli.McpServer.csproj -- --help >/dev/null
test -f .agent/PRD.md
test -f .agent/domain.md

echo "==> Alpha RC smoke check passed"
