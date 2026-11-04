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
        pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536; PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();
    }

    public override void BeforeSchema(DbConnection connection)
    {
        using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL";
        wal.ExecuteScalar();
    }

    public override void AfterSchema(DbConnection connection)
    {
        using var optimize = connection.CreateCommand();
        optimize.CommandText = "PRAGMA optimize";
        optimize.ExecuteNonQuery();
    }
}
