# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Repo rule

`AGENTS.md` is the only root Markdown entrypoint for agent instructions.

All other agent-readable Markdown belongs under `.agent/`. Do not create or rely on `docs/`, and do not scatter agent-readable Markdown files at the repo root.

## Layout

This repo uses a single-context domain-doc layout under `.agent/`.

Before exploring, read these when they exist:

- `.agent/CONTEXT.md` for project domain language and glossary.
- `.agent/adr/` for architectural decision records relevant to the area being changed.
- `.agent/PRD.md` when product intent, scope, or roadmap context matters.

If `.agent/CONTEXT.md` or `.agent/adr/` do not exist yet, proceed silently. Do not suggest creating them upfront unless the current task is explicitly about domain modeling, architecture documentation, or recording a decision.

## Expected structure

```text
/
├── AGENTS.md
├── .agent/
│   ├── CONTEXT.md
│   ├── PRD.md
│   ├── domain.md
│   ├── issue-tracker.md
│   ├── triage-labels.md
│   └── adr/
│       ├── 0001-example-decision.md
│       └── 0002-example-decision.md
└── src/
```

## Use the glossary's vocabulary

When output names a domain concept in an issue title, refactor proposal, hypothesis, test name, or implementation note, use the term as defined in `.agent/CONTEXT.md`.

If the concept is not in the glossary yet, treat that as a signal: either avoid inventing new language, or note the gap for a later domain-modeling pass.

## Flag ADR conflicts

If output contradicts an existing ADR under `.agent/adr/`, surface it explicitly rather than silently overriding it.
