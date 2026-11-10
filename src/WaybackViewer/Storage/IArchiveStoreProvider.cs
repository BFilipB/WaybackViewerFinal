using Microsoft.Data.Sqlite;

namespace WaybackViewer.Storage;

/// Creates an IArchiveStore for one named storage backend. To support another database, implement this
/// interface and register it with AddSingleton&lt;IArchiveStoreProvider, YourProvider&gt;(); then set Storage:Provider
/// to its Name.
public interface IArchiveStoreProvider
{
    string Name { get; }
    IArchiveStore Create(IConfiguration configuration);
}

public sealed class InMemoryArchiveStoreProvider : IArchiveStoreProvider
{
    public string Name => "Memory";
    public IArchiveStore Create(IConfiguration configuration)
    {
        var options = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        if (string.IsNullOrWhiteSpace(options.HtmlRoot)) return new InMemoryArchiveStore();
        var root = AppBuilder.ResolveDatabasePath(options.HtmlRoot);
        return new InMemoryArchiveStore(new HtmlFileStore(root));
    }
}

public sealed class PostgresArchiveStoreProvider : IArchiveStoreProvider
{
    public string Name => "Postgres";

    public IArchiveStore Create(IConfiguration configuration)
    {
        var options = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        var connectionString = configuration.GetConnectionString("WaybackDb");
        if (string.IsNullOrWhiteSpace(connectionString) || !connectionString.Contains('='))
            throw new InvalidOperationException(
                "Storage:Provider=Postgres needs ConnectionStrings:WaybackDb, e.g. Host=db;Database=wayback;Username=wayback;Password=...");
        var root = AppBuilder.ResolveDatabasePath(string.IsNullOrWhiteSpace(options.HtmlRoot) ? "html-payloads" : options.HtmlRoot);
        return new Sql.SqlArchiveStore(new Sql.PostgresDialect(), connectionString, new HtmlFileStore(root));
    }
}

public sealed class SqliteArchiveStoreProvider : IArchiveStoreProvider
{
    public string Name => "Sqlite";

    public IArchiveStore Create(IConfiguration configuration)
    {
        var options = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        var connectionString = configuration.GetConnectionString("WaybackDb");
        if (string.IsNullOrWhiteSpace(connectionString)) connectionString = "Data Source=wayback.db";

        var builder = new SqliteConnectionStringBuilder(connectionString);
        builder.DataSource = AppBuilder.ResolveDatabasePath(builder.DataSource);
        Console.WriteLine($"Database file: {builder.DataSource}");
        var root = ResolveHtmlRoot(options, builder.DataSource);
        return new SqliteArchiveStore(builder.ToString(), new HtmlFileStore(root));
    }

    private static string ResolveHtmlRoot(StorageOptions options, string? dbPath = null)
    {
        if (!string.IsNullOrWhiteSpace(options.HtmlRoot))
            return Path.IsPathRooted(options.HtmlRoot)
                ? options.HtmlRoot
                : AppBuilder.ResolveDatabasePath(options.HtmlRoot);

        if (!string.IsNullOrWhiteSpace(dbPath) && dbPath != ":memory:" && !dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrWhiteSpace(parent)) return Path.Combine(parent, "html-payloads");
        }

        return AppBuilder.ResolveDatabasePath("html-payloads");
    }
}
