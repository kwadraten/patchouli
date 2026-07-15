# Rust tools

Patchouli keeps Rust/Cargo as a supported repository toolchain for native or format-conversion helpers. Each helper belongs under `tools/<tool-name>`; Cargo build output is ignored through `tools/**/target/`.

CSL rendering itself runs in-process through the managed `Fsharp.Citeproc` package. A later bibliography-import change will add a Rust helper based on [`typst/biblatex`](https://github.com/typst/biblatex) for BibLaTeX-to-CSL conversion. Packaging scripts should only build and ship that helper once the application has a real runtime integration for it.
