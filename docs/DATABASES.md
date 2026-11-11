# Databases

WaybackViewer stores capture lists, saved pages, extracted text, popularity metadata and background operations behind one interface, `IArchiveStore`. Everything above it (controllers, `PageService`, `PreloadCoordinator`) is database-agnostic.

## Architecture

| Layer | Type | Role |
|---|---|---|
| Contract | `IArchiveStore` | Operations the app needs: searches, pages, metadata, operations |
| Provider selection | `IArchiveStoreProvider` | Chooses a store by the configured provider name |
| Generic relational store | `SqlArchiveStore` | All SQL logic, written once |
| Dialect | `SqlDialect` | Provider-specific SQL (types, upsert, paging, parameters) |
| SQLite | `SqliteDialect` | Default; single file, no server |
| PostgreSQL | `PostgresDialect` | Server database for multi-user and Docker deployments |
| Memory | `InMemoryArchiveStore` | Tests and throwaway runs |

HTML payloads can also be kept as files on disk; older rows stored as blobs are still read.

## Choosing a database

| Need | Use |
|---|---|
| Personal use, one machine, zero setup | SQLite |
| Several users or app instances, backups, larger archives | PostgreSQL |
| Automated tests, nothing persisted | Memory |

SQLite handles one writer at a time, so heavy parallel preloading on one file is slower than on PostgreSQL. For a single user it is usually enough.

## Configuration

Set the provider name and connection string in `appsettings.json`, or with environment variables (for example `Storage__Provider`). An unknown provider name fails at startup and lists the available ones.

## Docker

- SQLite: `docker compose up`
- PostgreSQL: `docker compose -f docker/compose.postgres.yml up`

## Adding a database

1. Add a class deriving from `SqlDialect` that supplies the provider SQL.
2. Register it with the provider selection so it has a name.
3. Run the shared store contract tests against it. The same scenarios run on SQLite, memory and PostgreSQL.

### SQL features the shared queries rely on

- `LIMIT @limit OFFSET @offset` paging.
- `LIKE ... ESCAPE '\'`: the Operations search escapes `\`, `%` and `_` with a backslash.
- `LOWER(...)`: the Operations search lowercases both sides because `LIKE` is case-sensitive on PostgreSQL.
- `MIN(CASE State WHEN ... END)` in `ORDER BY`: the Operations **State** sort (`OperationStates.SortRankSql`).

## Testing

- SQLite and memory contract tests run locally.
- PostgreSQL tests need `WAYBACK_TEST_POSTGRES` set to a connection string. Otherwise they are skipped. GitHub Actions provides a PostgreSQL service for them.
