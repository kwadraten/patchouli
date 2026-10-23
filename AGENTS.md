## Agent skills

### Issue tracker

Issues and PRDs are tracked in GitHub Issues. See `.agents/issue-tracker.md`.

### Domain docs

This repo uses a single-context domain-doc layout under `.agents/`; agent-readable Markdown belongs there rather than in the repo root or `docs/`. See `.agents/domain.md`.

### Repository automation

`scripts/` is the single root for repository automation, including PowerShell/Python scripts and compiled build-time utilities such as `scripts/Patchouli.ExtensionSchemas`. Do not create or restore a repository-root `tools/` directory. Application sidecars remain under `sidecar/`; temporary downloads and experiments belong under `.tmp/`.

### C# cleanup and analysis

Use JetBrains Command Line Tools version `2026.1.4` for repository cleanup and inspection. Do not use the default `Full Cleanup` profile or personal ReSharper settings.

The JetBrains command-line host has no F# source model (see `Directory.Build.targets`), so `scripts/cleanup-code.ps1` and `scripts/inspect-code.ps1` cover `.cs` and `.axaml` only. The correctness-gate split is:

- `.cs` / `.axaml` → C# compiler + the JetBrains suite below + tests.
- `.fs` → F# compiler diagnostics (`TreatWarningsAsErrors` in each `.fsproj`) + F# unit tests.
- `.fsx` → the production real-FCS policy checks in `src/Patchouli.Extensions/ExtensionCompiler.fs` + script execution tests.
- C#→F# boundary → compiled `Patchouli.Extensions.dll` metadata (`PatchouliUseBuiltExtensionAssembly`) + C# caller checks + interop tests.
- Fantomas, if used, is a formatting tool only and never a correctness gate.

After changing `.cs`/`.axaml` code or configuration, agents must run `scripts/cleanup-code.ps1` for the files they changed before tests. The script uses the repository `.editorconfig` and the fixed `Built-in: Reformat & Apply Syntax Style` profile. It intentionally excludes generated files and does not apply API-changing cleanup such as primary constructors, `init` properties, `field` keyword, member reordering, nullable fixes, async fixes, or visibility changes.

Pure `.fs`/`.fsx` changes that do not alter the public surface consumed by C# do not require the JetBrains suite. A change to the public interface of `Patchouli.Extensions` is a cross-language interface change: build `Patchouli.Extensions.dll` normally, rebuild the C# consumers, run `scripts/cleanup-code.ps1` on the changed callers and `scripts/inspect-code.ps1 -UseExistingBuild` (which reads the built assembly metadata), and run the interop tests.

Before any non-document Git commit, run `scripts/inspect-code.ps1`. Pure Markdown, text-only, and pure F# changes that keep the consumed public surface unchanged are exempt. The report is written to `.tmp/inspectcode.sarif` and ignored by Git. Inspection must have zero errors and no blocking rules: C# compiler warnings, XAML errors/possible null references, disposed or modified closures, async void methods/lambdas, empty general catches, possible multiple enumeration, and disposed-object access.

Do not add automatic suppressions. A verified false positive must use the narrowest ReSharper suppression at the affected code location with a short comment explaining why it is safe.

### Temporary files

The repository-root `.tmp/` is the only allowed root for temporary work: experiments, downloaded tools, scratch files, logs, inspection reports, test results, screenshots, performance reports, and isolated build/publish outputs. Use descriptive subdirectories and unique names under `.tmp/`; do not create or reuse `artifacts/`, `scratch/`, `temp/`, `tmp/`, or `TestResults/` as temporary roots.

All test projects must import `tests/Directory.Build.props` and its shared `RepositoryTestEnvironment`. It redirects process and child-process temporary files into `.tmp/tests/` before fixtures run. Tests must not override this to another temporary root, depend on files from deprecated temporary directories, or write previews/reports into them. Durable fixtures belong under `tests/`; durable agent documentation belongs under `.agents/`.

Tool-managed `bin/`, `obj/`, Rust `target/`, and `.codegraph/` retain their standard locations; do not use them for ad hoc temporary work.

### Test execution

Use `scripts/run-tests.ps1` for recorded test runs. `-Suite Full` runs ordinary, UI, and compiled-extension integration batches sequentially; normal correctness gates do not include high-count endurance loops. Performance/endurance probes have an explicit `-Suite Performance` entry point and are not part of Full. `-Suite All -Filter ...` is for focused development checks, not full acceptance. Reuse a current build with `-NoBuild -SkipRestore` after cleanup; rebuild whenever source or configuration changes. The F# compiler and compiled-extension integration checks remain correctness gates; the separate `tests/Patchouli.Extensions.Tests/Patchouli.Extensions.Tests.fsproj` owns compiled F# SDK tests and must also be validated when SDK code changes. Keep databases, configuration, extension state and cancellation state isolated per test; only immutable preparation and the existing headless session fixture may be shared.

### Version updates

Before increasing the application version, first write the corresponding version's update notes in `Changelog.md`. Only after that entry exists may you change version numbers in build configuration, application metadata, or packaging scripts. Keep those version numbers consistent with the changelog entry.
