# Keep Runtime Database Out Of Sync Roots

Status: accepted

The active working SQLite database lives in a local runtime directory and is not synced directly. Published snapshots are checkpointed into sync roots so cloud clients never race active WAL/SHM writes or corrupt the writer database.

**Considered Options**

- Put the active SQLite database directly in Google Drive, OneDrive, Dropbox, Syncthing, or NAS folders.
- Publish explicit snapshot artifacts into those folders.

**Consequences**

Sync is an explicit publish/import workflow rather than transparent live database sync.
