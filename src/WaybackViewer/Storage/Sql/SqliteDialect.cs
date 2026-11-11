using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace WaybackViewer.Storage.Sql;

public sealed class SqliteDialect : SqlDialect
{
    public override string Name => "Sqlite";

    public override DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

    public override IReadOnlyList<string> SchemaStatements => SqliteArchiveStore.SchemaStatements;

    public override void PrepareStorage(string connectionString)
    {
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (!string.IsNullOrWhiteSpace(path) && path != ":memory:" && !path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
    }

    public override void PrepareConnection(DbConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=FILE; PRAGMA cache_size=-8192; PRAGMA mmap_size=0; PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();
        ((Microsoft.Data.Sqlite.SqliteConnection)connection).CreateFunction("url_match", (string? q, string? v) => WaybackViewer.Models.SearchTextMatcher.Matches(q ?? string.Empty, v) ? 1 : 0);
    }

    public override bool SupportsUrlMatch => true;

    public override void BeforeSchema(DbConnection connection)
    {
        using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL";
        wal.ExecuteScalar();
    }

    public override void EnsureColumn(DbConnection connection, string table, string column, string type)
    {
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using var r = info.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    public override string Greatest(string a, string b) => $"MAX({a}, {b})";
    public override string Least(string a, string b) => $"MIN({a}, {b})";
    public override string TruncToInt(string expression) => $"CAST({expression} AS INTEGER)";

    public override void AfterSchema(DbConnection connection)
    {
        using var optimize = connection.CreateCommand();
        optimize.CommandText = "PRAGMA optimize";
        optimize.ExecuteNonQuery();
    }
}
