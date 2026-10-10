# ADR 0042: Statically declared workflow parameters

Status: accepted, 2026-10-10. Amends the definition and translation-default portions of ADR 0036 and the workflow authoring contract of ADR 0039.

## Decision

Workflow scripts declare independently named `Parameter<'T>` values through the SDK. A separate `WorkflowInfo` declares display information, applicable selection and menu placement; it does not collect parameters. Authors read values through `Parameter.get parameter input` and use the resulting ordinary F# values in their workflow. Stable explicit keys own saved configuration; F# binding names do not.

FSharp.Compiler.Service parses and checks the script. The declaration extractor recognizes resolved SDK symbols, static data, supported SDK constructors/modifiers and immutable aliases. It does not evaluate the script to discover fields. Unsupported declaration expressions produce source diagnostics. Business callbacks remain ordinary trusted F# code. The platform builds its own non-generic declaration projection for UI, validation and persistence; authors do not construct heterogeneous field lists or type-state builders.

Each workflow explicitly binds one model parameter through `Workflow.withModel`. The frozen choice reaches the actual model transport and context compactor. Stage-specific model switching is outside this change. Ordinary chat has a separate model selection; it does not borrow translation settings.

The generated form exists only in the workflow editor. Script locking and configuration editing are separate permissions. All declared fields are displayed in source order. Context-bound fields show their source and cannot overwrite the current selection.

Host validation precedes session creation. Failure reports stable field keys, codes, messages and source locations. Desktop launch reports the error in the status bar, opens the appropriate editor, preserves drafts and highlights the affected fields. Editing revalidates the field; saving never automatically launches the workflow. MCP uses the same validator and returns field diagnostics without requiring the desktop UI.

Translation is an ordinary workflow. Its language, window radius and previous-window backfill defaults belong to its script. Provider connections and credentials remain shared platform configuration.

## Persistence and migration

Script declaration and user values are stored separately. Old definition metadata is migration input or a rebuildable projection, never a competing source of parameter definitions. Script identity, declaration fingerprint, resolved values, selection and model choice are frozen for new sessions under workflow API version 6.

Old translation values and custom workflow metadata are migrated with backups without overwriting existing new configuration. Pending legacy values survive a settings save before migration. Credentials are never copied into workflow values.

Version 5 and older snapshots retain readable history but have no compatibility execution path. Authors revise unsupported scripts and start new sessions. Version 6 recovery uses frozen inputs rather than current defaults or settings.

## Consequences

New workflow fields require only script declarations. Static authoring has an intentionally restricted declaration syntax; it does not promise evaluation of arbitrary F# expressions. No arbitrary source rewriting, global parameter injection or type provider is required. Configuration errors are repairable in the same editor for built-in and user workflows.
