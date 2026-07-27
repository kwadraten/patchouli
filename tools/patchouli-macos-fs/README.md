# patchouli-macos-fs

A tiny Objective-C helper that exposes a C API for macOS-specific file system operations that .NET cannot handle portably:

- Resolving POSIX symlinks **and** Finder aliases.
- Materializing iCloud Drive placeholders before Patchouli opens them.

The library is intentionally minimal and has no external dependencies beyond `Foundation.framework`.

## API

See `patchouli_macos_fs.h`.

## Building

For local development on an Apple Silicon Mac:

```bash
clang -dynamiclib -framework Foundation \
  -arch arm64 \
  -install_name @rpath/libpatchouli-macos-fs.dylib \
  -o libpatchouli-macos-fs.dylib \
  patchouli_macos_fs.m
```

`scripts/package-macos.sh` builds the helper for the requested architecture (or as a universal binary) and copies it into `Patchouli.Net.app/Contents/MacOS/`.

## License

GPLv3, matching the rest of the Patchouli project.
