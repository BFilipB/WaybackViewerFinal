using Microsoft.Data.Sqlite;
using WaybackViewer.Models;
using WaybackViewer.Parsing;

namespace WaybackViewer.Storage;

/// SQLite implementation: one local file, nothing to install. WAL mode lets reads run while a write is in
/// progress; all writes are serialised through one lock and done inside a single transaction per call, which is
/// what makes saving thousands of rows take milliseconds instead of seconds.
public sealed class SqliteArchiveStore : IArchiveStore
{
    private static readonly string[] SchemaStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS Captures (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            CaptureTs  TEXT NOT NULL,
            Original   TEXT NOT NULL,
            MimeType   TEXT NOT NULL,
            StatusCode TEXT NOT NULL,
            UNIQUE (CaptureTs, Original))
        """,
        """
        CREATE TABLE IF NOT EXISTS Searches (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            UrlPrefix   TEXT NOT NULL,
            Filter      TEXT NOT NULL,
            CachedAtUtc INTEGER NOT NULL,
            UNIQUE (UrlPrefix, Filter))
        """,
        """
        CREATE TABLE IF NOT EXISTS SearchCaptures (
            SearchId  INTEGER NOT NULL,
            CaptureId INTEGER NOT NULL,
            PRIMARY KEY (SearchId, CaptureId)) WITHOUT ROWID
        """,
        // Small metadata row (cheap "is it saved?" check), text and compressed HTML kept in separate tables
        // so listing and text queries never read the big HTML blobs.
        """
        CREATE TABLE IF NOT EXISTS Pages (
            CaptureId    INTEGER PRIMARY KEY,
            Title        TEXT,
            Description  TEXT,
            FetchedAtUtc INTEGER NOT NULL)
        """,
        "CREATE TABLE IF NOT EXISTS PageText (CaptureId INTEGER PRIMARY KEY, Body TEXT NOT NULL)",
        "CREATE TABLE IF NOT EXISTS PageHtml (CaptureId INTEGER PRIMARY KEY, HtmlGz BLOB NOT NULL)",
        """
        CREATE TABLE IF NOT EXISTS Operations (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            UrlPrefix   TEXT NOT NULL,
            Filter      TEXT NOT NULL,
            Kind        INTEGER NOT NULL,
            State       INTEGER NOT NULL,
            CreatedAt   INTEGER NOT NULL,
            UpdatedAt   INTEGER NOT NULL,
            Total       INTEGER NOT NULL,
            Completed   INTEGER NOT NULL,
            Failed      INTEGER NOT NULL,
            Error       TEXT,
            UNIQUE (UrlPrefix, Filter, Kind))
        """,
        "CREATE INDEX IF NOT EXISTS IX_SearchCaptures_CaptureId ON SearchCaptures (CaptureId)",
        "CREATE INDEX IF NOT EXISTS IX_Operations_State ON Operations (State, UpdatedAt)"
    ];

    private readonly string connectionString;
    private readonly HtmlFileStore htmlFiles;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    // ---- in-memory snapshot cache -------------------------------------------------------------------------
    // A capture list is read from SQLite once and then served from memory: clicking a page number or a filter no
    // longer re-reads (and re-sorts) 100,000 rows. Which captures are saved is tracked in one id set that is
    // updated whenever pages are written, so the "saved" flags stay correct without touching the list.
    private const int MaxCachedSearches = 4;
    private readonly object cacheLock = new();
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private readonly Dictionary<string, CachedSearch> searchCache = new(StringComparer.Ordinal);
    private HashSet<long>? savedIds;
    private long savedVersion;
    private long useTick;

    private sealed class CachedSearch(long searchId, DateTimeOffset cachedAt, long[] ids, Capture[] captures)
    {
        public readonly long SearchId = searchId;
        public readonly DateTimeOffset CachedAt = cachedAt;
        public readonly long[] Ids = ids;
        public readonly Capture[] Captures = captures;
        public SearchSnapshot? Composed;
        public long ComposedVersion = -1;
        public long LastUsed;
    }

    private static string CacheKey(string prefix, string filter) => filter + "|" + prefix;

    public SqliteArchiveStore(string connectionString)
        : this(connectionString, new HtmlFileStore(AppBuilder.ResolveDatabasePath("html-payloads")))
    {
    }

    public SqliteArchiveStore(string connectionString, HtmlFileStore htmlFiles)
    {
        this.connectionString = connectionString;
        this.htmlFiles = htmlFiles;
    }

    public Task InitializeAsync(CancellationToken ct = default) => WriteAsync(() =>
    {
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (!string.IsNullOrWhiteSpace(path) && path != ":memory:" && !path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        using var conn = Open();
        using (var wal = Cmd(conn, null, "PRAGMA journal_mode=WAL")) wal.ExecuteScalar();
        foreach (var sql in SchemaStatements)
        {
            using var cmd = Cmd(conn, null, sql);
            cmd.ExecuteNonQuery();
        }
        using (var optimize = Cmd(conn, null, "PRAGMA optimize")) optimize.ExecuteNonQuery();
        return 0;
    }, ct);

    public async Task<PagedSearchResult?> GetSearchPageAsync(string prefix, string filter, int offset, int limit,
        CancellationToken ct = default)
    {
        var snapshot = await GetCachedSnapshotAsync(prefix, filter, ct).ConfigureAwait(false);
        if (snapshot is null) return null;
        var slice = snapshot.Captures.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).ToList();
        return new PagedSearchResult(snapshot.SearchId, snapshot.CachedAt, slice, snapshot.Captures.Count);
    }

    public Task<BackgroundOperation?> FindActiveOperationAsync(string prefix, string filter, OperationKind kind,
        CancellationToken ct = default) => Task.Run(() =>
        {
            using var conn = Open();
            using var cmd = Cmd(conn, null, "SELECT * FROM Operations WHERE UrlPrefix = $p AND Filter = $f AND Kind = $k");
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$f", filter);
            cmd.Parameters.AddWithValue("$k", (int)kind);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var operation = ReadOperation(reader);
            return operation.IsActive ? operation : null;
        }, ct);

    public Task<BackgroundOperation> CreateOrGetOperationAsync(string prefix, string filter, OperationKind kind,
        DateTimeOffset now, CancellationToken ct = default) => WriteAsync(() =>
        {
            using var conn = Open();
            using var find = Cmd(conn, null, "SELECT * FROM Operations WHERE UrlPrefix = $p AND Filter = $f AND Kind = $k");
            find.Parameters.AddWithValue("$p", prefix);
            find.Parameters.AddWithValue("$f", filter);
            find.Parameters.AddWithValue("$k", (int)kind);
            using (var reader = find.ExecuteReader())
            {
                if (reader.Read())
                {
                    var existing = ReadOperation(reader);
                    if (existing.IsActive) return existing;
                }
            }

            using var cmd = Cmd(conn, null, """
            INSERT INTO Operations (UrlPrefix, Filter, Kind, State, CreatedAt, UpdatedAt, Total, Completed, Failed, Error)
            VALUES ($p, $f, $k, $state, $created, $updated, 0, 0, 0, NULL)
            ON CONFLICT (UrlPrefix, Filter, Kind) DO UPDATE SET
                State = excluded.State, CreatedAt = excluded.CreatedAt, UpdatedAt = excluded.UpdatedAt,
                Total = 0, Completed = 0, Failed = 0, Error = NULL
            """);
            var stamp = now.ToUnixTimeMilliseconds();
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$f", filter);
            cmd.Parameters.AddWithValue("$k", (int)kind);
            cmd.Parameters.AddWithValue("$state", (int)OperationState.Queued);
            cmd.Parameters.AddWithValue("$created", stamp);
            cmd.Parameters.AddWithValue("$updated", stamp);
            cmd.ExecuteNonQuery();

            using var result = Cmd(conn, null, "SELECT * FROM Operations WHERE UrlPrefix = $p AND Filter = $f AND Kind = $k");
            result.Parameters.AddWithValue("$p", prefix);
            result.Parameters.AddWithValue("$f", filter);
            result.Parameters.AddWithValue("$k", (int)kind);
            using var row = result.ExecuteReader();
            row.Read();
            return ReadOperation(row);
        }, ct);

    public Task<BackgroundOperation?> GetOperationAsync(long id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = Cmd(conn, null, "SELECT * FROM Operations WHERE Id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadOperation(reader) : null;
    }, ct);

    public Task<IReadOnlyList<BackgroundOperation>> GetOperationsAsync(bool activeOnly = false,
        CancellationToken ct = default) => Task.Run(() =>
        {
            using var conn = Open();
            var sql = activeOnly
                ? "SELECT * FROM Operations WHERE State IN (0, 1, 2, 3, 5) ORDER BY UpdatedAt DESC"
                : "SELECT * FROM Operations ORDER BY UpdatedAt DESC";
            using var cmd = Cmd(conn, null, sql);
            using var reader = cmd.ExecuteReader();
            var result = new List<BackgroundOperation>();
            while (reader.Read()) result.Add(ReadOperation(reader));
            return (IReadOnlyList<BackgroundOperation>)result;
        }, ct);

    public Task UpdateOperationAsync(BackgroundOperation operation, CancellationToken ct = default) => WriteAsync(() =>
    {
        using var conn = Open();
        using var cmd = Cmd(conn, null, """
            UPDATE Operations SET State = $state, UpdatedAt = $updated, Total = $total,
                Completed = $completed, Failed = $failed, Error = $error WHERE Id = $id
            """);
        cmd.Parameters.AddWithValue("$id", operation.Id);
        cmd.Parameters.AddWithValue("$state", (int)operation.State);
        cmd.Parameters.AddWithValue("$updated", operation.UpdatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$total", operation.Total);
        cmd.Parameters.AddWithValue("$completed", operation.Completed);
        cmd.Parameters.AddWithValue("$failed", operation.Failed);
        cmd.Parameters.AddWithValue("$error", (object?)operation.Error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return 0;
    }, ct);

    public Task<SearchSnapshot?> GetSearchAsync(string prefix, string filter, CancellationToken ct = default) =>
        GetCachedSnapshotAsync(prefix, filter, ct);

    private async Task<SearchSnapshot?> GetCachedSnapshotAsync(string prefix, string filter, CancellationToken ct)
    {
        var key = CacheKey(prefix, filter);
        var hit = TryGetCached(key);
        if (hit is not null) return hit;

        await loadGate.WaitAsync(ct).ConfigureAwait(false);   // one cold load at a time; the rest wait and then hit the cache
        try
        {
            hit = TryGetCached(key);
            if (hit is not null) return hit;

            return await Task.Run(() =>
            {
                using var conn = Open();
                long id;
                long cachedAtMs;
                using (var find = Cmd(conn, null, "SELECT Id, CachedAtUtc FROM Searches WHERE UrlPrefix = $p AND Filter = $f"))
                {
                    find.Parameters.AddWithValue("$p", prefix);
                    find.Parameters.AddWithValue("$f", filter);
                    using var reader = find.ExecuteReader();
                    if (!reader.Read()) return (SearchSnapshot?)null;
                    id = reader.GetInt64(0);
                    cachedAtMs = reader.GetInt64(1);
                }

                var loaded = LoadCachedSearch(conn, id, DateTimeOffset.FromUnixTimeMilliseconds(cachedAtMs));
                UrlClassifier.Warm(loaded.Captures.Select(c => c.Original).ToArray()); // parallel, so the first filter click is fast too
                EnsureSavedIds(conn);
                lock (cacheLock)
                {
                    loaded.LastUsed = ++useTick;
                    searchCache[key] = loaded;
                    while (searchCache.Count > MaxCachedSearches)
                    {
                        var oldest = searchCache.MinBy(p => p.Value.LastUsed).Key;
                        searchCache.Remove(oldest);
                    }
                    return Compose(loaded);
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            loadGate.Release();
        }
    }

    private SearchSnapshot? TryGetCached(string key)
    {
        lock (cacheLock)
        {
            if (savedIds is null || !searchCache.TryGetValue(key, out var cached)) return null;
            cached.LastUsed = ++useTick;
            return Compose(cached);
        }
    }

    /// Builds (or reuses) the snapshot with current "saved" flags. Reused until a page is written.
    private SearchSnapshot Compose(CachedSearch cached)
    {
        lock (cacheLock)
        {
            if (cached.Composed is not null && cached.ComposedVersion == savedVersion) return cached.Composed;
            var saved = savedIds!;
            var list = new List<StoredCapture>(cached.Ids.Length);
            for (var i = 0; i < cached.Ids.Length; i++)
                list.Add(new StoredCapture(cached.Ids[i], cached.Captures[i], saved.Contains(cached.Ids[i])));
            cached.Composed = new SearchSnapshot(cached.SearchId, cached.CachedAt, list);
            cached.ComposedVersion = savedVersion;
            return cached.Composed;
        }
    }

    private void EnsureSavedIds(SqliteConnection conn)
    {
        lock (cacheLock)   // held while reading so a concurrent SavePages cannot be missed (see MarkSaved)
        {
            if (savedIds is not null) return;
            var set = new HashSet<long>();
            using var cmd = Cmd(conn, null, "SELECT CaptureId FROM Pages");
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) set.Add(reader.GetInt64(0));
            savedIds = set;
            savedVersion++;
        }
    }

    private void MarkSaved(List<long> ids)
    {
        lock (cacheLock)
        {
            if (savedIds is null) return;   // not loaded yet: the first load reads everything that is committed
            foreach (var id in ids) savedIds.Add(id);
            savedVersion++;
        }
    }

    public async Task<SearchSnapshot> SaveSearchAsync(string prefix, string filter, IReadOnlyList<Capture> captures,
        DateTimeOffset cachedAt, CancellationToken ct = default)
    {
        var searchId = await WriteAsync(() =>
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            long id;
            using (var upsert = Cmd(conn, tx, """
                INSERT INTO Searches (UrlPrefix, Filter, CachedAtUtc) VALUES ($p, $f, $at)
                ON CONFLICT (UrlPrefix, Filter) DO UPDATE SET CachedAtUtc = excluded.CachedAtUtc
                """))
            {
                upsert.Parameters.AddWithValue("$p", prefix);
                upsert.Parameters.AddWithValue("$f", filter);
                upsert.Parameters.AddWithValue("$at", cachedAt.ToUnixTimeMilliseconds());
                upsert.ExecuteNonQuery();
            }
            using (var find = Cmd(conn, tx, "SELECT Id FROM Searches WHERE UrlPrefix = $p AND Filter = $f"))
            {
                find.Parameters.AddWithValue("$p", prefix);
                find.Parameters.AddWithValue("$f", filter);
                id = Convert.ToInt64(find.ExecuteScalar());
            }
            using (var clear = Cmd(conn, tx, "DELETE FROM SearchCaptures WHERE SearchId = $id"))
            {
                clear.Parameters.AddWithValue("$id", id);
                clear.ExecuteNonQuery();
            }

            using var insertCapture = Cmd(conn, tx,
                "INSERT OR IGNORE INTO Captures (CaptureTs, Original, MimeType, StatusCode) VALUES ($ts, $o, $m, $s)");
            var cTs = insertCapture.Parameters.Add("$ts", SqliteType.Text);
            var cO = insertCapture.Parameters.Add("$o", SqliteType.Text);
            var cM = insertCapture.Parameters.Add("$m", SqliteType.Text);
            var cS = insertCapture.Parameters.Add("$s", SqliteType.Text);

            using var insertMember = Cmd(conn, tx, """
                INSERT OR IGNORE INTO SearchCaptures (SearchId, CaptureId)
                SELECT $id, Id FROM Captures WHERE CaptureTs = $ts AND Original = $o
                """);
            insertMember.Parameters.AddWithValue("$id", id);
            var mTs = insertMember.Parameters.Add("$ts", SqliteType.Text);
            var mO = insertMember.Parameters.Add("$o", SqliteType.Text);

            var n = 0;
            foreach (var c in captures)
            {
                if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
                cTs.Value = mTs.Value = c.Timestamp;
                cO.Value = mO.Value = c.Original;
                cM.Value = c.MimeType;
                cS.Value = c.StatusCode;
                insertCapture.ExecuteNonQuery();
                insertMember.ExecuteNonQuery();
            }

            tx.Commit();
            return id;
        }, ct);

        lock (cacheLock) searchCache.Remove(CacheKey(prefix, filter));   // the list changed: drop the old copy
        return (await GetCachedSnapshotAsync(prefix, filter, ct).ConfigureAwait(false))!;
    }

    public Task<StoredPage?> GetPageAsync(string timestamp, string original, bool includeHtml, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var conn = Open();
            var sql = """
                SELECT c.CaptureTs, c.Original, c.MimeType, c.StatusCode, p.Title, p.Description, t.Body, p.FetchedAtUtc
                """ + (includeHtml ? ", h.HtmlGz" : "") + """

                FROM Captures c
                JOIN Pages p ON p.CaptureId = c.Id
                JOIN PageText t ON t.CaptureId = c.Id
                """ + (includeHtml ? "\nJOIN PageHtml h ON h.CaptureId = c.Id" : "") + """

                WHERE c.CaptureTs = $ts AND c.Original = $o
                """;
            using var cmd = Cmd(conn, null, sql);
            cmd.Parameters.AddWithValue("$ts", timestamp);
            cmd.Parameters.AddWithValue("$o", original);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var capture = new Capture(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            var parsed = new ParsedPage(
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6));
            var fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7));
            byte[]? html = null;
            if (includeHtml)
            {
                html = htmlFiles.LoadAsync(capture, ct).GetAwaiter().GetResult();
                if (html is null && !reader.IsDBNull(8))
                {
                    var legacy = reader.GetFieldValue<byte[]>(8);
                    if (legacy.Length > 0) html = legacy;
                }
            }
            return (StoredPage?)new StoredPage(capture, html, parsed, fetchedAt);
        }, ct);

    public Task SavePagesAsync(IReadOnlyList<NewPage> pages, CancellationToken ct = default) =>
        pages.Count == 0 ? Task.CompletedTask : WriteAsync(() =>
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            using var insertCapture = Cmd(conn, tx,
                "INSERT OR IGNORE INTO Captures (CaptureTs, Original, MimeType, StatusCode) VALUES ($ts, $o, $m, $s)");
            var cTs = insertCapture.Parameters.Add("$ts", SqliteType.Text);
            var cO = insertCapture.Parameters.Add("$o", SqliteType.Text);
            var cM = insertCapture.Parameters.Add("$m", SqliteType.Text);
            var cS = insertCapture.Parameters.Add("$s", SqliteType.Text);

            using var findId = Cmd(conn, tx, "SELECT Id FROM Captures WHERE CaptureTs = $ts AND Original = $o");
            var fTs = findId.Parameters.Add("$ts", SqliteType.Text);
            var fO = findId.Parameters.Add("$o", SqliteType.Text);

            using var insertPage = Cmd(conn, tx,
                "INSERT OR REPLACE INTO Pages (CaptureId, Title, Description, FetchedAtUtc) VALUES ($id, $t, $d, $at)");
            var pId = insertPage.Parameters.Add("$id", SqliteType.Integer);
            var pT = insertPage.Parameters.Add("$t", SqliteType.Text);
            var pD = insertPage.Parameters.Add("$d", SqliteType.Text);
            var pAt = insertPage.Parameters.Add("$at", SqliteType.Integer);

            using var insertText = Cmd(conn, tx, "INSERT OR REPLACE INTO PageText (CaptureId, Body) VALUES ($id, $b)");
            var tId = insertText.Parameters.Add("$id", SqliteType.Integer);
            var tB = insertText.Parameters.Add("$b", SqliteType.Text);

            using var insertHtml = Cmd(conn, tx, "INSERT OR REPLACE INTO PageHtml (CaptureId, HtmlGz) VALUES ($id, $h)");
            var hId = insertHtml.Parameters.Add("$id", SqliteType.Integer);
            var hH = insertHtml.Parameters.Add("$h", SqliteType.Blob);

            var newIds = new List<long>(pages.Count);
            foreach (var page in pages)
            {
                cTs.Value = fTs.Value = page.Capture.Timestamp;
                cO.Value = fO.Value = page.Capture.Original;
                cM.Value = page.Capture.MimeType;
                cS.Value = page.Capture.StatusCode;
                insertCapture.ExecuteNonQuery();
                var id = Convert.ToInt64(findId.ExecuteScalar());
                newIds.Add(id);

                pId.Value = tId.Value = hId.Value = id;
                pT.Value = (object?)page.Parsed.Title ?? DBNull.Value;
                pD.Value = (object?)page.Parsed.Description ?? DBNull.Value;
                pAt.Value = page.FetchedAt.ToUnixTimeMilliseconds();
                tB.Value = page.Parsed.Text;
                htmlFiles.SaveAsync(page.Capture, page.HtmlGz, ct).GetAwaiter().GetResult();
                hH.Value = Array.Empty<byte>();
                insertPage.ExecuteNonQuery();
                insertText.ExecuteNonQuery();
                insertHtml.ExecuteNonQuery();
            }

            tx.Commit();
            MarkSaved(newIds);
            return 0;
        }, ct);

    // ---- helpers -----------------------------------------------------------------------------------------

    private async Task<T> WriteAsync<T>(Func<T> work, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, ct).ConfigureAwait(false); }
        finally { writeLock.Release(); }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536; PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static SqliteCommand Cmd(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static CachedSearch LoadCachedSearch(SqliteConnection conn, long searchId, DateTimeOffset cachedAt)
    {
        using var cmd = Cmd(conn, null, """
            SELECT c.Id, c.CaptureTs, c.Original, c.MimeType, c.StatusCode
            FROM SearchCaptures sc
            JOIN Captures c ON c.Id = sc.CaptureId
            WHERE sc.SearchId = $id
            ORDER BY c.CaptureTs DESC, c.Id DESC
            """);
        cmd.Parameters.AddWithValue("$id", searchId);

        var ids = new List<long>();
        var captures = new List<Capture>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
            captures.Add(new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        return new CachedSearch(searchId, cachedAt, ids.ToArray(), captures.ToArray());
    }

    private static BackgroundOperation ReadOperation(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        (OperationKind)reader.GetInt32(3),
        (OperationState)reader.GetInt32(4),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.GetInt32(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));
}
