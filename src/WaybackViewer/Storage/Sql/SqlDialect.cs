using System.Data.Common;

namespace WaybackViewer.Storage.Sql;

/// Everything that differs between SQL databases. SqlArchiveStore contains all the logic (caching, batching,
/// locking, paging) and asks the dialect only for: how to connect, the schema, and the upsert / insert-ignore
/// syntax. To support another database, derive from this class (see docs/DATABASES.md).
///
/// SQL text uses "@name" parameters and ANSI-style quoting-free identifiers, which every ADO.NET driver accepts.
public abstract class SqlDialect
{
    public abstract string Name { get; }

    public abstract DbConnection CreateConnection(string connectionString);

    /// The CREATE TABLE / CREATE INDEX statements, each safe to run repeatedly (IF NOT EXISTS).
    public abstract IReadOnlyList<string> SchemaStatements { get; }

    /// Runs once before opening connections (for example creating the database file's folder).
    public virtual void PrepareStorage(string connectionString) { }

    /// Runs on every freshly opened connection (for example SQLite PRAGMAs).
    public virtual void PrepareConnection(DbConnection connection) { }

    /// Runs once before the schema statements.
    public virtual void BeforeSchema(DbConnection connection) { }

    /// Runs once after the schema statements.
    public virtual void AfterSchema(DbConnection connection) { }

    /// "INSERT ... ON CONFLICT DO NOTHING". Works on SQLite and PostgreSQL; override for other databases
    /// (SQL Server: IF NOT EXISTS / MERGE, MySQL: INSERT IGNORE).
    /// <paramref name="source"/> is either "VALUES (@a, @b)" or a "SELECT ..." statement.
    public virtual string InsertIgnore(string table, string[] columns, string source, string[] conflictKey) =>
        $"INSERT INTO {table} ({string.Join(", ", columns)}) {source} ON CONFLICT ({string.Join(", ", conflictKey)}) DO NOTHING";

    /// Insert, or update the existing row with the same key. Parameters are named after the columns (@Column).
    public virtual string Upsert(string table, string[] columns, string[] key, string[]? update = null)
    {
        update ??= columns.Except(key, StringComparer.OrdinalIgnoreCase).ToArray();
        var values = string.Join(", ", columns.Select(c => "@" + c));
        var head = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({values}) ON CONFLICT ({string.Join(", ", key)})";
        return update.Length == 0
            ? head + " DO NOTHING"
            : head + " DO UPDATE SET " + string.Join(", ", update.Select(c => $"{c} = excluded.{c}"));
    }
}
