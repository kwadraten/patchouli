# Use Dapper And Manual SQL

Status: accepted

Infrastructure uses Dapper and handwritten SQL rather than EF Core because SQLite shards, FTS, migrations, append/revision semantics, dirty queues, and snapshot manifests need explicit control.

**Consequences**

The code accepts more SQL ownership in exchange for predictable storage behavior and easier inspection of durable invariants.
