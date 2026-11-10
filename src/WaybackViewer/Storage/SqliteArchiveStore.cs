using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WaybackViewer.Models;
using WaybackViewer.Parsing;

namespace WaybackViewer.Storage;

/// SQLite implementation: one local file, nothing to install. WAL mode lets reads run while a write is in
/// progress; all writes are serialised through one lock and done inside a single transaction per call, which is
/// what makes saving thousands of rows take milliseconds instead of seconds.
public sealed partial class SqliteArchiveStore : IArchiveStore
{
    internal static readonly string[] SchemaStatements =
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
        "CREATE TABLE IF NOT EXISTS PageMetadata (CaptureId INTEGER PRIMARY KEY, Views INTEGER, Likes INTEGER, Shares INTEGER, Comments INTEGER, Score INTEGER, EngagementScore INTEGER, Source TEXT, Author TEXT, PublishedAtUtc INTEGER, Tags TEXT, Confidence REAL)",
        "CREATE TABLE IF NOT EXISTS CaptureSearch (CaptureId INTEGER PRIMARY KEY, SearchText TEXT NOT NULL)",
        "CREATE INDEX IF NOT EXISTS IX_CaptureSearch_SearchText ON CaptureSearch(SearchText)",
        // Per-list archive popularity fallback (see ArchiveSignals), rebuilt when the list is refreshed.
        "CREATE TABLE IF NOT EXISTS CaptureSignals (SearchId INTEGER NOT NULL, CaptureId INTEGER NOT NULL, Host TEXT NOT NULL, Raw INTEGER NOT NULL, PRIMARY KEY (SearchId, CaptureId)) WITHOUT ROWID",
        "CREATE TABLE IF NOT EXISTS SearchSignals (SearchId INTEGER PRIMARY KEY, CachedAtUtc INTEGER NOT NULL, Total INTEGER NOT NULL)",
        "CREATE TABLE IF NOT EXISTS PageHtml (CaptureId INTEGER PRIMARY KEY, HtmlGz BLOB NOT NULL)",
        // Terminal preload result of captures that are not saved (skipped / failed); saved = a Pages row.
        "CREATE TABLE IF NOT EXISTS CaptureOutcomes (CaptureId INTEGER PRIMARY KEY, Outcome INTEGER NOT NULL, Reason TEXT NOT NULL, UpdatedAtUtc INTEGER NOT NULL)",
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
        "CREATE INDEX IF NOT EXISTS IX_Operations_State ON Operations (State, UpdatedAt)",
        "CREATE INDEX IF NOT EXISTS IX_Operations_UpdatedAt ON Operations (UpdatedAt)",
        "CREATE TABLE IF NOT EXISTS SearchSummary (SearchId INTEGER PRIMARY KEY, CaptureCount INTEGER NOT NULL, FirstTs TEXT, LastTs TEXT)",
        "CREATE INDEX IF NOT EXISTS IX_SearchSummary_Count ON SearchSummary (CaptureCount)",
        "CREATE INDEX IF NOT EXISTS IX_Searches_Cached ON Searches (CachedAtUtc)"
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
        public readonly HashSet<long> Saved = new();   // saved captures of this list only
        public HashSet<long>? Members;
    }

    public Task<IReadOnlyDictionary<string, PageMetadata>> GetPopularityMetadataAsync(string prefix, string filter, CancellationToken ct = default) =>
        Task.Run<IReadOnlyDictionary<string, PageMetadata>>(() =>
        {
            using var conn = Open();
            using var cmd = Cmd(conn, null, """
                SELECT c.CaptureTs, c.Original, m.Views, m.Likes, m.Shares, m.Comments, m.Score, m.EngagementScore,
                       m.Source, m.Author, m.PublishedAtUtc, m.Tags, m.Confidence
                FROM Searches s
                JOIN SearchCaptures sc ON sc.SearchId = s.Id
                JOIN Captures c ON c.Id = sc.CaptureId
                JOIN PageMetadata m ON m.CaptureId = c.Id
                WHERE s.UrlPrefix = $p AND s.Filter = $f
                """);
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$f", filter);
            using var reader = cmd.ExecuteReader();
            var result = new Dictionary<string, PageMetadata>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var key = $"{reader.GetString(0)}|{reader.GetString(1)}";
                result[key] = new PageMetadata(
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? 0 : reader.GetDouble(12));
            }
            return result;
        }, ct);

    private static string CacheKey(string prefix, string filter) => filter + "|" + prefix;

    /// Optional diagnostics sink, set by StoreInitializer; the store works without it.
    public ILogger? Log { get; set; }

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
        EnsureClassificationSchema(conn);
        EnsureMetadataVersionColumn(conn);
        ClassifyPending(conn, ct);   // one-time: Year/Category/Section/Kind/GroupKey for captures saved before they existed
        UrlQueries.Backfill(conn);   // one-time: summary rows for searches saved before the summary table existed
        using (var optimize = Cmd(conn, null, "PRAGMA optimize")) optimize.ExecuteNonQuery();
        return 0;
    }, ct);

    public async Task<PagedSearchResult?> GetSearchPageAsync(string prefix, string filter, int offset, int limit,
        CancellationToken ct = default)
    {
		var cached = TryGetCached(CacheKey(prefix, filter));
		if (cached is not null)
		{
			var slice = cached.Captures.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).ToList();
			return new PagedSearchResult(cached.SearchId, cached.CachedAt, slice, cached.Captures.Count);
		}
		// Not cached: page in SQL (indexed ORDER BY + LIMIT, EXISTS for "saved") instead of loading the whole list.
		return await Task.Run(() => ReadPageFromSql(prefix, filter, Math.Max(0, offset), Math.Max(0, limit)), ct).ConfigureAwait(false);
	}

	private PagedSearchResult? ReadPageFromSql(string prefix, string filter, int offset, int limit)
	{
		using var conn = Open();
		long id, cachedAtMs;
		using (var find = Cmd(conn, null, "SELECT Id, CachedAtUtc FROM Searches WHERE UrlPrefix = $p AND Filter = $f"))
		{
			find.Parameters.AddWithValue("$p", prefix);
			find.Parameters.AddWithValue("$f", filter);
			using var r = find.ExecuteReader();
			if (!r.Read()) return null;
			id = r.GetInt64(0);
			cachedAtMs = r.GetInt64(1);
		}
		long total;
		using (var count = Cmd(conn, null, "SELECT COUNT(*) FROM SearchCaptures WHERE SearchId = $id"))
		{
			count.Parameters.AddWithValue("$id", id);
			total = (long)count.ExecuteScalar()!;
		}
		var rows = new List<StoredCapture>(limit);
		if (limit > 0)
		{
			using var cmd = Cmd(conn, null, """
				SELECT c.Id, c.CaptureTs, c.Original, c.MimeType, c.StatusCode,
					   EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id)
				FROM SearchCaptures sc
				JOIN Captures c ON c.Id = sc.CaptureId
				WHERE sc.SearchId = $id
				ORDER BY c.CaptureTs DESC, c.Id DESC
				LIMIT $limit OFFSET $offset
				""");
			cmd.Parameters.AddWithValue("$id", id);
			cmd.Parameters.AddWithValue("$limit", limit);
			cmd.Parameters.AddWithValue("$offset", offset);
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
				rows.Add(new StoredCapture(reader.GetInt64(0),
					new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)),
					reader.GetInt64(5) != 0));
		}
		return new PagedSearchResult(id, DateTimeOffset.FromUnixTimeMilliseconds(cachedAtMs), rows, (int)total);
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
                ? "SELECT * FROM Operations WHERE State IN (" + OperationStates.ActiveSqlList + ") ORDER BY UpdatedAt DESC"
                : "SELECT * FROM Operations ORDER BY UpdatedAt DESC";
            using var cmd = Cmd(conn, null, sql);
            using var reader = cmd.ExecuteReader();
            var result = new List<BackgroundOperation>();
            while (reader.Read()) result.Add(ReadOperation(reader));
            return (IReadOnlyList<BackgroundOperation>)result;
        }, ct);

    public Task<IReadOnlyList<BackgroundOperation>> GetOperationsForSearchAsync(string prefix, string filter,
        CancellationToken ct = default) => Task.Run(() =>
        {
            using var conn = Open();
            return OperationQueries.ForSearch(conn, prefix, filter, r => ReadOperation((Microsoft.Data.Sqlite.SqliteDataReader)r));
        }, ct);

    public Task<int> CountOtherActiveSearchesAsync(string prefix, string filter, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var conn = Open();
            return OperationQueries.CountOtherActive(conn, prefix, filter);
        }, ct);

    public Task<OperationSearchPage> ListOperationSearchesAsync(OperationListQuery query, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var conn = Open();
            return OperationQueries.ListSearches(conn, query);
        }, ct);

    public Task<IReadOnlyList<BackgroundOperation>> GetOperationsForSearchesAsync(IReadOnlyCollection<SearchKey> searches,
        CancellationToken ct = default) => Task.Run(() =>
        {
            using var conn = Open();
            return OperationQueries.ForSearches(conn, searches, r => ReadOperation((Microsoft.Data.Sqlite.SqliteDataReader)r));
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
                lock (cacheLock)   // read under the lock so a concurrent MarkSaved cannot be missed
                {
                    LoadSaved(conn, loaded);
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
            if (!searchCache.TryGetValue(key, out var cached)) return null;
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
            var saved = cached.Saved;
            var list = new List<StoredCapture>(cached.Ids.Length);
            for (var i = 0; i < cached.Ids.Length; i++)
                list.Add(new StoredCapture(cached.Ids[i], cached.Captures[i], saved.Contains(cached.Ids[i])));
            cached.Composed = new SearchSnapshot(cached.SearchId, cached.CachedAt, list);
            cached.ComposedVersion = savedVersion;
            return cached.Composed;
        }
    }

    private static void LoadSaved(SqliteConnection conn, CachedSearch cached)
    {
        using var cmd = Cmd(conn, null, "SELECT sc.CaptureId FROM SearchCaptures sc JOIN Pages p ON p.CaptureId = sc.CaptureId WHERE sc.SearchId = $id");
        cmd.Parameters.AddWithValue("$id", cached.SearchId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) cached.Saved.Add(reader.GetInt64(0));
    }

    private void MarkSaved(List<long> ids)
    {
        lock (cacheLock)
        {
            foreach (var cached in searchCache.Values)
            {
                cached.Members ??= new HashSet<long>(cached.Ids);
                foreach (var id in ids) if (cached.Members.Contains(id)) cached.Saved.Add(id);
            }
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

            UrlQueries.RefreshSummary(conn, tx, id);
            tx.Commit();
            ClassifyPending(conn, ct);
            return id;
        }, ct);

        lock (cacheLock) searchCache.Remove(CacheKey(prefix, filter));   // the list changed: drop the old copy
        return (await GetCachedSnapshotAsync(prefix, filter, ct).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<long>> MatchCaptureIdsAsync(string query, CancellationToken ct = default)
    {
        var tokens = SearchTextMatcher.IndexTokens(query);
        if (tokens.Count == 0) return Array.Empty<long>();

        return await Task.Run(() =>
        {
            using var conn = Open();
            var conditions = new List<string>();
            for (var i = 0; i < tokens.Count; i++)
            {
                conditions.Add("SearchText LIKE '%' || $t" + i + " || '%'");
            }

            using var cmd = Cmd(conn, null, "SELECT CaptureId FROM CaptureSearch WHERE " + string.Join(" AND ", conditions));
            for (var i = 0; i < tokens.Count; i++)
                cmd.Parameters.AddWithValue("$t" + i, tokens[i]);

            var ids = new List<long>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
            return (IReadOnlyList<long>)ids;
        }, ct).ConfigureAwait(false);
    }

    public Task<StoredPage?> GetPageAsync(string timestamp, string original, bool includeHtml, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var conn = Open();
            var sql = """
                SELECT c.CaptureTs, c.Original, c.MimeType, c.StatusCode, p.Title, p.Description, t.Body, p.FetchedAtUtc,
                       m.Views, m.Likes, m.Shares, m.Comments, m.Score, m.EngagementScore, m.Source, m.Author, m.PublishedAtUtc, m.Tags, m.Confidence
                """ + (includeHtml ? ", h.HtmlGz" : "") + """

                FROM Captures c
                JOIN Pages p ON p.CaptureId = c.Id
                JOIN PageText t ON t.CaptureId = c.Id
                LEFT JOIN PageMetadata m ON m.CaptureId = c.Id
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
                reader.GetString(6),
                reader.IsDBNull(8) ? null : new PageMetadata(
                    reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    reader.IsDBNull(12) ? null : reader.GetInt64(12),
                    reader.IsDBNull(13) ? null : reader.GetInt64(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15),
                    reader.IsDBNull(16) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(16)),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.IsDBNull(18) ? 0 : reader.GetDouble(18)));
            var fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7));
            byte[]? html = null;
            if (includeHtml)
            {
                html = htmlFiles.LoadAsync(capture, ct).GetAwaiter().GetResult();
                if (html is null && !reader.IsDBNull(19))
                {
                    var legacy = reader.GetFieldValue<byte[]>(19);
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

            var saveStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long payloadTicks = 0, payloadBytes = 0;
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

            using var upsertMetadata = Cmd(conn, tx, "INSERT OR REPLACE INTO PageMetadata (CaptureId, Views, Likes, Shares, Comments, Score, EngagementScore, Source, Author, PublishedAtUtc, Tags, Confidence, ExtractorVersion) VALUES ($id, $views, $likes, $shares, $comments, $score, $engagement, $source, $author, $published, $tags, $confidence, $ev)");
            var mId = upsertMetadata.Parameters.Add("$id", SqliteType.Integer);
            var mViews = upsertMetadata.Parameters.Add("$views", SqliteType.Integer);
            var mLikes = upsertMetadata.Parameters.Add("$likes", SqliteType.Integer);
            var mShares = upsertMetadata.Parameters.Add("$shares", SqliteType.Integer);
            var mComments = upsertMetadata.Parameters.Add("$comments", SqliteType.Integer);
            var mScore = upsertMetadata.Parameters.Add("$score", SqliteType.Integer);
            var mEngagement = upsertMetadata.Parameters.Add("$engagement", SqliteType.Integer);
            var mSource = upsertMetadata.Parameters.Add("$source", SqliteType.Text);
            var mAuthor = upsertMetadata.Parameters.Add("$author", SqliteType.Text);
            var mPublished = upsertMetadata.Parameters.Add("$published", SqliteType.Integer);
            var mTags = upsertMetadata.Parameters.Add("$tags", SqliteType.Text);
            var mConfidence = upsertMetadata.Parameters.Add("$confidence", SqliteType.Real);
            var mVersion = upsertMetadata.Parameters.Add("$ev", SqliteType.Integer);
            mVersion.Value = ExtractorVersion.Current;

            using var upsertIndex = Cmd(conn, tx, "INSERT OR REPLACE INTO CaptureSearch (CaptureId, SearchText) VALUES ($id, $s)");
            var sId = upsertIndex.Parameters.Add("$id", SqliteType.Integer);
            var sText = upsertIndex.Parameters.Add("$s", SqliteType.Text);

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

                pId.Value = tId.Value = hId.Value = sId.Value = mId.Value = id;
                pT.Value = (object?)page.Parsed.Title ?? DBNull.Value;
                pD.Value = (object?)page.Parsed.Description ?? DBNull.Value;
                pAt.Value = page.FetchedAt.ToUnixTimeMilliseconds();
                tB.Value = page.Parsed.Text;
                sText.Value = SearchTextMatcher.BuildIndexText(page.Capture.Original, page.Parsed.Title, page.Parsed.Description, page.Parsed.Text);
                var metadata = page.Parsed.Metadata;
                mViews.Value = DbValue(metadata?.Views);
                mLikes.Value = DbValue(metadata?.Likes);
                mShares.Value = DbValue(metadata?.Shares);
                mComments.Value = DbValue(metadata?.Comments);
                mScore.Value = DbValue(metadata?.Score);
                mEngagement.Value = DbValue(metadata?.EngagementScore);
                mSource.Value = (object?)metadata?.Source ?? DBNull.Value;
                mAuthor.Value = (object?)metadata?.Author ?? DBNull.Value;
                mPublished.Value = metadata?.PublishedAt?.ToUnixTimeMilliseconds() is long published ? published : DBNull.Value;
                mTags.Value = (object?)metadata?.Tags ?? DBNull.Value;
                mConfidence.Value = metadata is null ? DBNull.Value : metadata.Confidence;
                var fileStart = System.Diagnostics.Stopwatch.GetTimestamp();
                htmlFiles.SaveAsync(page.Capture, page.HtmlGz, ct).GetAwaiter().GetResult();
                payloadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - fileStart;
                payloadBytes += page.HtmlGz.Length;
                hH.Value = Array.Empty<byte>();
                insertPage.ExecuteNonQuery();
                insertText.ExecuteNonQuery();
                if (metadata is not null) upsertMetadata.ExecuteNonQuery();
                else
                {
                    using var deleteMetadata = Cmd(conn, tx, "DELETE FROM PageMetadata WHERE CaptureId = $id");
                    deleteMetadata.Parameters.AddWithValue("$id", id);
                    deleteMetadata.ExecuteNonQuery();
                }
                upsertIndex.ExecuteNonQuery();
                insertHtml.ExecuteNonQuery();
            }

            var commitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            tx.Commit();
            if (Log is { } saveLog)
            {
                var totalMs = System.Diagnostics.Stopwatch.GetElapsedTime(saveStart).TotalMilliseconds;
                var payloadMs = payloadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                saveLog.Log(totalMs > 1000 ? LogLevel.Warning : LogLevel.Information,
                    "store.save_pages pages={Pages} totalMs={Total:F0} payloadFileMs={FileMs:F0} payloadBytes={Bytes} commitMs={Commit:F0}",
                    pages.Count, totalMs, payloadMs, payloadBytes, System.Diagnostics.Stopwatch.GetElapsedTime(commitStart).TotalMilliseconds);
            }
            MarkSaved(newIds);
            return 0;
        }, ct);

    // ---- helpers -----------------------------------------------------------------------------------------

    public Task SaveOutcomesAsync(IReadOnlyList<CaptureOutcome> outcomes, CancellationToken ct = default) =>
        outcomes.Count == 0 ? Task.CompletedTask : WriteAsync(() =>
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var insertCapture = Cmd(conn, tx,
                "INSERT OR IGNORE INTO Captures (CaptureTs, Original, MimeType, StatusCode) VALUES ($ts, $o, $m, $s)");
            var cTs = insertCapture.Parameters.Add("$ts", SqliteType.Text);
            var cO = insertCapture.Parameters.Add("$o", SqliteType.Text);
            var cM = insertCapture.Parameters.Add("$m", SqliteType.Text);
            var cS = insertCapture.Parameters.Add("$s", SqliteType.Text);
            using var upsert = Cmd(conn, tx, """
                INSERT OR REPLACE INTO CaptureOutcomes (CaptureId, Outcome, Reason, UpdatedAtUtc)
                SELECT Id, $outcome, $reason, $at FROM Captures WHERE CaptureTs = $ts AND Original = $o
                """);
            var uTs = upsert.Parameters.Add("$ts", SqliteType.Text);
            var uO = upsert.Parameters.Add("$o", SqliteType.Text);
            var uOutcome = upsert.Parameters.Add("$outcome", SqliteType.Integer);
            var uReason = upsert.Parameters.Add("$reason", SqliteType.Text);
            var uAt = upsert.Parameters.Add("$at", SqliteType.Integer);
            foreach (var outcome in outcomes)
            {
                cTs.Value = uTs.Value = outcome.Capture.Timestamp;
                cO.Value = uO.Value = outcome.Capture.Original;
                cM.Value = outcome.Capture.MimeType;
                cS.Value = outcome.Capture.StatusCode;
                insertCapture.ExecuteNonQuery();
                uOutcome.Value = (int)outcome.Outcome;
                uReason.Value = outcome.Reason;
                uAt.Value = outcome.At.ToUnixTimeMilliseconds();
                upsert.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        }, ct);

    public Task<IReadOnlyDictionary<string, CaptureOutcome>> GetOutcomesAsync(string prefix, string filter, CancellationToken ct = default) =>
        Task.Run<IReadOnlyDictionary<string, CaptureOutcome>>(() =>
        {
            using var conn = Open();
            using var cmd = Cmd(conn, null, """
                SELECT c.CaptureTs, c.Original, c.MimeType, c.StatusCode, o.Outcome, o.Reason, o.UpdatedAtUtc
                FROM Searches s
                JOIN SearchCaptures sc ON sc.SearchId = s.Id
                JOIN CaptureOutcomes o ON o.CaptureId = sc.CaptureId
                JOIN Captures c ON c.Id = o.CaptureId
                WHERE s.UrlPrefix = $p AND s.Filter = $f
                  AND NOT EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = o.CaptureId)
                """);
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$f", filter);
            using var reader = cmd.ExecuteReader();
            var result = new Dictionary<string, CaptureOutcome>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var capture = new Capture(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
                result[capture.Key] = new CaptureOutcome(capture, (PreloadOutcome)reader.GetInt32(4), reader.GetString(5),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)));
            }
            return result;
        }, ct);

    public Task<int> ResetFailedAsync(string prefix, string filter, CancellationToken ct = default) => WriteAsync(() =>
    {
        using var conn = Open();
        using var cmd = Cmd(conn, null, """
            DELETE FROM CaptureOutcomes WHERE Outcome = $failed AND CaptureId IN (
                SELECT sc.CaptureId FROM Searches s JOIN SearchCaptures sc ON sc.SearchId = s.Id
                WHERE s.UrlPrefix = $p AND s.Filter = $f)
            """);
        cmd.Parameters.AddWithValue("$failed", (int)PreloadOutcome.Failed);
        cmd.Parameters.AddWithValue("$p", prefix);
        cmd.Parameters.AddWithValue("$f", filter);
        return cmd.ExecuteNonQuery();
    }, ct);

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
        pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456; PRAGMA wal_autocheckpoint=4000; PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();
        // Free-text URL filter: the C# matcher runs inside SQL so paging stays bounded.
        conn.CreateFunction("url_match", (string? q, string? v) => SearchTextMatcher.Matches(q ?? string.Empty, v) ? 1 : 0);
        return conn;
    }

    private static SqliteCommand Cmd(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static object DbValue(long? value) => value.HasValue ? value.Value : DBNull.Value;

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
