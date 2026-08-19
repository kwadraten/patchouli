## Agent skills

### Issue tracker

Issues and PRDs are tracked in GitHub Issues; external PRs are not a triage surface. See `.agents/issue-tracker.md`.

### Triage labels

Triage uses the default five-label vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `.agents/triage-labels.md`.

### Domain docs

This repo uses a single-context domain-doc layout under `.agents/`; agent-readable Markdown belongs there rather than in the repo root or `docs/`. See `.agents/domain.md`.

### C# cleanup and analysis

Use JetBrains Command Line Tools version `2026.1.4` for repository cleanup and inspection. Do not use the default `Full Cleanup` profile or personal ReSharper settings.

After changing non-document code or configuration, agents must run `scripts/cleanup-code.ps1` for the files they changed before tests. The script uses the repository `.editorconfig` and the fixed `Built-in: Reformat & Apply Syntax Style` profile. It intentionally excludes generated files and does not apply API-changing cleanup such as primary constructors, `init` properties, `field` keyword, member reordering, nullable fixes, async fixes, or visibility changes.

Before any non-document Git commit, run `scripts/inspect-code.ps1`. Pure Markdown and text-only changes are exempt. The report is written to `artifacts/inspectcode.sarif` and ignored by Git. Inspection must have zero errors and no blocking rules: C# compiler warnings, XAML errors/possible null references, disposed or modified closures, async void methods/lambdas, empty general catches, possible multiple enumeration, and disposed-object access.

Do not add automatic suppressions. A verified false positive must use the narrowest ReSharper suppression at the affected code location with a short comment explaining why it is safe.
