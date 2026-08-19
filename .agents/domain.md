# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Repo rule

`AGENTS.md` is the only root Markdown entrypoint for agent instructions.

All other agent-readable Markdown belongs under `.agents/`. Do not create or rely on `docs/`, and do not scatter agent-readable Markdown files at the repo root.

## Layout

This repo uses a single-context domain-doc layout under `.agents/`.

Before exploring, read these when they exist:

- `.agents/CONTEXT.md` for project domain language and glossary.
- `.agents/adr/` for architectural decision records relevant to the area being changed.
- `.agents/PRD.md` when product intent, scope, or roadmap context matters.

If `.agents/CONTEXT.md` or `.agents/adr/` do not exist yet, proceed silently. Do not suggest creating them upfront unless the current task is explicitly about domain modeling, architecture documentation, or recording a decision.

## Expected structure

```text
/
├── AGENTS.md
├── .agents/
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

When output names a domain concept in an issue title, refactor proposal, hypothesis, test name, or implementation note, use the term as defined in `.agents/CONTEXT.md`.

If the concept is not in the glossary yet, treat that as a signal: either avoid inventing new language, or note the gap for a later domain-modeling pass.

## Flag ADR conflicts

If output contradicts an existing ADR under `.agents/adr/`, surface it explicitly rather than silently overriding it.
