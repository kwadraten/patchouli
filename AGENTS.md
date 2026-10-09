## Agent skills

### Issue tracker

Issues and PRDs are tracked in GitHub Issues. See `.agents/issue-tracker.md`.

### Domain docs

This repo uses a single-context domain-doc layout under `.agents/`; agent-readable Markdown belongs there rather than in the repo root or `docs/`. See `.agents/domain.md`.

### C# cleanup and analysis

Use JetBrains Command Line Tools version `2026.1.4` for repository cleanup and inspection. Do not use the default `Full Cleanup` profile or personal ReSharper settings.

After changing non-document code or configuration, agents must run `scripts/cleanup-code.ps1` for the files they changed before tests. The script uses the repository `.editorconfig` and the fixed `Built-in: Reformat & Apply Syntax Style` profile. It intentionally excludes generated files and does not apply API-changing cleanup such as primary constructors, `init` properties, `field` keyword, member reordering, nullable fixes, async fixes, or visibility changes.

Before any non-document Git commit, run `scripts/inspect-code.ps1`. Pure Markdown and text-only changes are exempt. The report is written to `.tmp/inspectcode.sarif` and ignored by Git. Inspection must have zero errors and no blocking rules: C# compiler warnings, XAML errors/possible null references, disposed or modified closures, async void methods/lambdas, empty general catches, possible multiple enumeration, and disposed-object access.

Do not add automatic suppressions. A verified false positive must use the narrowest ReSharper suppression at the affected code location with a short comment explaining why it is safe.

### Temporary files

The repository-root `.tmp/` is the only allowed root for temporary work: experiments, downloaded tools, scratch files, logs, inspection reports, test results, screenshots, performance reports, and isolated build/publish outputs. Use descriptive subdirectories and unique names under `.tmp/`; do not create or reuse `artifacts/`, `scratch/`, `temp/`, `tmp/`, or `TestResults/` as temporary roots.

All test projects must import `tests/Directory.Build.props` and its shared `RepositoryTestEnvironment`. It redirects process and child-process temporary files into `.tmp/tests/` before fixtures run. Tests must not override this to another temporary root, depend on files from deprecated temporary directories, or write previews/reports into them. Durable fixtures belong under `tests/`; durable agent documentation belongs under `.agents/`.

Tool-managed `bin/`, `obj/`, Rust `target/`, and `.codegraph/` retain their standard locations; do not use them for ad hoc temporary work.

### Version updates

Before increasing the application version, first write the corresponding version's update notes in `Changelog.md`. Only after that entry exists may you change version numbers in build configuration, application metadata, or packaging scripts. Keep those version numbers consistent with the changelog entry.
