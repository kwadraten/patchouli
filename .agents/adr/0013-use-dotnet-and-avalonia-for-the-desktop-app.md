# Use .NET And Avalonia For The Desktop App

Status: accepted

The v1 desktop app uses the .NET ecosystem and native Avalonia because the product is desktop-first and needs mature SQLite access, Windows-friendly packaging, file dialogs, keyboard-heavy workflows, and local background workers. F# remains preferred where it improves domain modeling or state transitions; C# remains acceptable where UI bindings and ecosystem ergonomics lower risk.

**Considered Options**

- .NET with Avalonia.
- Tauri/Rust.
- Electron/TypeScript.
