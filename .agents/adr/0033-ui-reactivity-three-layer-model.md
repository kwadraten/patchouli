# 0033. UI Reactivity Three-Layer Model

Date: 2026-09-13

## Status

Accepted

## Context

We need a clear architecture for UI reactivity to separate concerns, ensure deterministic notification ordering, and improve performance while avoiding duplicate property change notifications or recursive cycles. Previously, `PropertyChanged.SourceGenerator` was used, but it lacked the specific boundaries we need for cross-object and async reactivity versus pure static derived properties.

## Decision

We adopt a three-layer model for UI reactivity:

1. **DerivedPropertyGenerator**: A repository-owned Roslyn incremental generator handles only static, synchronous, same-instance property dependencies.
   - It analyzes partial ViewModels based on CommunityToolkit.Mvvm `ObservableObject` / `ViewModelBase`.
   - It infers only bare/this same-instance property references in pure computed getters to build direct edges and transitive closures, emitting a deterministic topological dependent notification order.
   - Generated `OnPropertyChanged` overrides call `base` for the original notification, then call `base` directly for derived notifications to avoid recursion/duplicates.
   - Inherited source properties are supported.
   - Deterministic diagnostics prevent cycles, backing-field bypass, non-partial participating classes, conflicting overrides, and unsafe computed getters (collections, cross-object, async).

2. **System.Reactive (Rx)**: Handles time, async, collection, cross-object, and event relations.
   - `Throttle` or `Debounce` followed by `Switch` for latest-wins semantics.
   - `Buffer` followed by ID merge and `Concat` for non-droppable commit flows.
   - Normal Desktop does not poll; polling is for explicit abnormal recovery only.
   - Rx outputs are exposed as public read-only / private-set properties, ensuring UI scheduling, distinctness (`DistinctUntilChanged`), centralized error handling, and lifecycle disposal.

3. **CommunityToolkit.Mvvm**: Handles mutable View Model state and commands.
   - `[ObservableProperty]` is used for mutable state (must be partial-property form).
   - Async and Relay commands delegate to `IAsyncRelayCommand`/`IRelayCommand` semantics, exposing execution state and cancellation.

## Consequences

- `PropertyChanged.SourceGenerator` is removed from central packages and the UI project.
- A new Roslyn generator project `Patchouli.UI.Generators` is added and wired as an Analyzer.
- ViewModels manage Rx subscriptions idempotently (e.g., using `CompositeDisposable`), explicitly registering them for deterministic cleanup.
