# macOS File System Access Uses TCC Picker, No App Sandbox, No App Store

Status: accepted

Patchouli desktop on macOS will not use App Sandbox entitlements and will not target the Mac App Store for the foreseeable future. Instead, it relies on the standard macOS Transparency, Consent, and Control (TCC) folder access prompt, triggered through Avalonia's `IStorageProvider.OpenFolderPickerAsync`. This matches the product assumption that users keep PDF source files in arbitrary user-managed locations and that Patchouli must read them without moving them into an app container.

**Considered Options**

- App Sandbox + security-scoped bookmarks + Mac App Store distribution: rejected because it would complicate access to arbitrary user folders, restrict automation and the local MCP server, and add distribution overhead that is unnecessary for a GPLv3 desktop research tool.
- Non-sandboxed app + TCC picker + direct file system access: accepted as the minimal viable model that preserves user control over file placement.
- Non-sandboxed app + manual POSIX path access without a picker: rejected because TCC prompts are still required for Documents, Desktop, and Downloads on modern macOS, and silent failures would degrade the user experience.

**Consequences**

- Packaging does not need a sandbox entitlements file or an App Store provisioning profile. The existing `scripts/package-macos.sh` does not run any explicit codesign step; the DMG is distributed as-is for testing.
- `packaging/macos/Info.plist.template` must keep `NSDocumentsFolderUsageDescription`, `NSDesktopFolderUsageDescription`, `NSDownloadsFolderUsageDescription`, `NSNetworkVolumesUsageDescription`, and `NSRemovableVolumesUsageDescription` so the TCC prompt shows a useful message.
- `FileSearchRoot` records created from a macOS picker selection should use `AuthorizationKind = "tcc_picker"` to record provenance. No security-scoped bookmark payload is required because the app is not sandboxed.
- A macOS-specific `INativeFileAccessAdapter` implementation must handle Finder aliases, iCloud placeholder materialization, mount-point/unmount detection, TCC/ACL denial classification, and symlink canonicalization. The portable adapter is intentionally incomplete for these cases.
- Codesign is intentionally out of scope: `scripts/package-macos.sh` does not invoke `codesign`, so the produced `.app` and `.dmg` are distributed as-is for testing. The .NET SDK and clang already attach default ad-hoc signatures to the binaries they emit, which is sufficient for local use but not for notarized/Gatekeeper-clean distribution.
- MCP server and local automation paths keep unrestricted file system access as far as TCC allows, because the app is not sandboxed.

**Implementation Plan**

1. Packaging simplification
   - Remove any sandbox/entitlements placeholders from the macOS packaging directory.
   - Document in `scripts/package-macos.sh` that no `codesign` step is performed and that the DMG is distributed as-is for testing.
   - Do not add an entitlements file and do not add any `codesign` invocations.
   - Keep `NS*FolderUsageDescription` keys in `Info.plist.template`; add a comment in `scripts/package-macos.sh` explaining why they are still required outside the sandbox.

2. Picker-based root selection
   - `PathPickerTextBox` on macOS already uses `IStorageProvider.OpenFolderPickerAsync`. Ensure it produces a `SelectedFileSearchRoot` with `AuthorizationKind = "tcc_picker"`, `ProviderIdentity = "avalonia_storage_provider"`, and a UTC `SelectedAt` timestamp.
   - Before persisting a new root, `FileResolutionService.AddSearchRootAsync` should attempt a lightweight probe (`Directory.EnumerateFileSystemEntries` on the picked path) and report `access_denied` immediately if TCC was denied.
   - The picker itself is the user grant; no security-scoped bookmark is persisted.

3. TCC/ACL denial
   - `MacOSNativeFileAccessAdapter.ResolveDirectoryAsync` must catch `UnauthorizedAccessException` and return `access_denied` with a user-readable reason.
   - The UI must surface the recovery action: open **System Settings > Privacy & Security > Files and Folders** and grant Patchouli access, or pick a different folder.
   - ACL-only restrictions (POSIX ACL or SIP-protected directories) must be classified the same way as TCC denials for UX purposes.

4. Symlink, Finder alias, and mount point
   - Symlinks are already canonicalized in `PortableNativeFileAccessAdapter.ResolveDirectoryAsync` via `DirectoryInfo.ResolveLinkTarget(true)`. Keep this behavior on macOS.
   - Finder aliases require a macOS-specific adapter. Implement a small native helper using Objective-C runtime interop or a tiny Swift/C helper in `tools/patchouli-macos-fs` that resolves an alias path to its target. The C# adapter calls this helper and falls back to `offline` if resolution fails.
   - Mount points (network volumes, removable drives) are treated as ordinary directories during traversal. If a mount disappears mid-scan, the resulting `DirectoryNotFoundException` becomes `offline` for that branch.
   - Cycle detection in `FileSearchRootAccess.TraverseAsync` already uses a `HashSet` of resolved paths; extend it to report cycles as `skippedDirectories` with code `directory_cycle` instead of silently dropping them.

5. iCloud placeholder materialization
   - `MacOSNativeFileAccessAdapter.MaterializeFileAsync` must detect iCloud Drive placeholders and trigger download.
   - Use `NSFileCoordinator` and `NSURL.StartDownloadingUbiquitousItem` (or an equivalent Objective-C runtime call) with a timeout and cancellation token.
   - Return states:
     - `IsAvailable = true` when the file is local or successfully downloaded.
     - `FailureCode = "icloud_not_downloaded"` if the download could not start or the user is offline.
     - `FailureCode = "offline"` for network volume disconnects.
   - PDF import and page rendering must call `MaterializeFileAsync` before opening the file with PDFium.

6. Cancellation and offline handling
   - All native adapter methods accept `CancellationToken` and throw `OperationCanceledException` when cancelled.
   - `FileSearchRootAccess.TraverseAsync` and `ScanPdfAsync` already check `CancellationToken.IsCancellationRequested` between entries and return `FileSearchRootScanStatuses.Cancelled`.
   - Ensure native helpers do not ignore cancellation: pass the token into wait loops and timeouts.
   - When a root becomes offline (unmounted, directory deleted, iCloud unavailable), the root row status becomes `offline` and the next rescan retries automatically or on user action.

7. Verification checklist
   - Add an integration test that creates a picker-originated `SelectedFileSearchRoot` under `~/Documents` and verifies PDF discovery can enumerate at least one PDF.
   - Add unit tests for `MacOSNativeFileAccessAdapter` using mock `NSURL` behavior where possible; at minimum test the classification of `UnauthorizedAccessException`, `DirectoryNotFoundException`, and iCloud placeholder detection.
   - Update `AlphaPackagingTests` to assert that `Info.plist.template` contains the TCC usage descriptions and that `scripts/package-macos.sh` does not reference an entitlements file.
