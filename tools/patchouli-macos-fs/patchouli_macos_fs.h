#ifndef PATCHOULI_MACOS_FS_H
#define PATCHOULI_MACOS_FS_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Resolves the real filesystem path for `path`, following POSIX symlinks and
 * macOS Finder aliases. The resolved UTF-8 path is written to `out`.
 *
 * Returns:
 *   0  - success; `out` contains the resolved path
 *   -1 - failure; `err` contains a user-readable message
 *
 * `out_len` and `err_len` include the null terminator.
 */
int patchouli_resolve_path(const char* path, char* out, size_t out_len, char* err, size_t err_len);

/*
 * Ensures the file at `path` is available locally. If the file is an iCloud
 * Drive placeholder, this starts the download and waits up to `timeout_ms`
 * milliseconds for it to complete.
 *
 * Returns:
 *   0  - the file is available locally; `out` contains the path
 *   1  - the file is an iCloud placeholder that could not be materialized
 *        within the timeout (e.g. offline or user cancelled)
 *   -1 - failure; `err` contains a user-readable message
 *
 * `out_len` and `err_len` include the null terminator.
 */
int patchouli_materialize_file(const char* path, char* out, size_t out_len, char* err, size_t err_len,
    unsigned int timeout_ms);

#ifdef __cplusplus
}
#endif

#endif
