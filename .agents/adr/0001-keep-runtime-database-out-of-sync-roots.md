# Keep Runtime Database Out Of Sync Roots

Status: accepted

The active working SQLite database lives in a local runtime directory and is not synced directly. Published snapshots are checkpointed into sync roots so cloud clients never race active WAL/SHM writes or corrupt the writer database.

**Considered Options**

- Put the active SQLite database directly in Google Drive, OneDrive, Dropbox, Syncthing, or NAS folders.
- Publish explicit snapshot artifacts into those folders.

**Consequences**

Sync is an explicit publish/import workflow rather than transparent live database sync.

**Standing Constraints**

- The runtime database may use WAL/SHM locally, but those active files are never the sync artifact.
- Publishing writes candidate snapshot artifacts, validates them, and then advances the current pointer.
- Importing a snapshot validates into a temporary area before applying it to the active library.
- Runtime cache paths and page render outputs are not copied into snapshot payloads.
