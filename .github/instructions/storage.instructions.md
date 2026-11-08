---
applyTo: "src/WaybackViewer/Storage/**"
---
# Storage rules
- SQL must be parameterized and go through the `SqlDialect` classes so SQLite and Postgres both work.
- Page in the database (LIMIT/OFFSET or keyset), not by loading full lists and using `Skip/Take` in memory.
- Prefer `EXISTS`/indexed lookups over loading whole ID sets into memory.
- No sync-over-async; file I/O is async with reasonable buffer sizes.
- Schema changes must be additive and handled in schema initialization; keep existing data readable.
- Add tests in `StoreTests` / `GenericSqlStoreTests` for new queries.
