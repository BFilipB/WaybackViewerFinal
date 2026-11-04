using System.Data;
using System.Data.Common;
using WaybackViewer.Models;
using WaybackViewer.Parsing;

namespace WaybackViewer.Storage.Sql;

/// One IArchiveStore for every relational database that has an ADO.NET driver. All behaviour (snapshot cache,
/// write serialisation, batching, paging) lives here; everything database-specific is in the SqlDialect.
/// SQL uses "@name" parameters and "INSERT ... ON CONFLICT" through the dialect.
public sealed class SqlArchiveStore : IArchiveStore
{
    private readonly SqlDialect dialect;
    private readonly string connectionString;
    private readonly HtmlFileStore htmlFiles;
    private readonly SemaphoreSlim writeLock = new(1, 1);

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

    public SqlArchiveStore(SqlDialect dialect, string connectionString, HtmlFileStore htmlFiles)
    {
        this.dialect = dialect;
        this.connectionString = connectionString;
        this.htmlFiles = htmlFiles;
    }

    private static string CacheKey(string prefix, string filter) => filter + "|" + prefix;

    // ---- schema -------------------------------------------------------------------------------------------

    public Task InitializeAsync(CancellationToken ct = default) => WriteAsync(() =>
    {
        dialect.PrepareStorage(connectionString);
        using var conn = Open();
        dialect.BeforeSchema(conn);
        foreach (var sql in dialect.SchemaStatements)
        {
            using var cmd = Cmd(conn, null, sql);
            cmd.ExecuteNonQuery();
        }
        dialect.AfterSchema(conn);
        return 0;
    }, ct);

    // ---- searches -----------------------------------------------------------------------------------------

    public Task<SearchSnapshot?> GetSearchAsync(string prefix, string filter, CancellationToken ct = default) =>
        GetCachedSnapshotAsync(prefix, filter, ct);

    public async Task<PagedSearchResult?> GetSearchPageAsync(string prefix, string filter, int offset, int limit,
        CancellationToken ct = default)
    {
        var snapshot = await GetCachedSnapshotAsync(prefix, filter, ct).ConfigureAwait(false);
        if (snapshot is null) return null;
        var slice = snapshot.Captures.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).ToList();
        return new PagedSearchResult(snapshot.SearchId, snapshot.CachedAt, slice, snapshot.Captures.Count);
    }

    private async Task<SearchSnapshot?> GetCachedSnapshotAsync(string prefix, string filter, CancellationToken ct)
    {
        var key = CacheKey(prefix, filter);
        var hit = TryGetCached(key);
        if (hit is not null) return hit;

        await loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            hit = TryGetCached(key);
            if (hit is not null) return hit;

            return await Task.Run(() =>
            {
                using var conn = Open();
                long id;
                long cachedAtMs;
                using (var find = Cmd(conn, null, "SELECT Id, CachedAtUtc FROM Searches WHERE UrlPrefix = @p AND Filter = @f"))
                {
                    Param(find, "@p", prefix);
                    Param(find, "@f", filter);
                    using var reader = find.ExecuteReader();
                    if (!reader.Read()) return (SearchSnapshot?)null;
                    id = reader.GetInt64(0);
                    cachedAtMs = reader.GetInt64(1);
                }

                var loaded = LoadCachedSearch(conn, id, DateTimeOffset.FromUnixTimeMilliseconds(cachedAtMs));
                UrlClassifier.Warm(loaded.Captures.Select(c => c.Original).ToArray());
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

    private void EnsureSavedIds(DbConnection conn)
    {
        lock (cacheLock)
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
            if (savedIds is null) return;
            foreach (var id in ids) savedIds.Add(id);
            savedVersion++;
        }
    }

    private CachedSearch LoadCachedSearch(DbConnection conn, long searchId, DateTimeOffset cachedAt)
    {
        using var cmd = Cmd(conn, null, """
            SELECT c.Id, c.CaptureTs, c.Original, c.MimeType, c.StatusCode
            FROM SearchCaptures sc
            JOIN Captures c ON c.Id = sc.CaptureId
            WHERE sc.SearchId = @id
            ORDER BY c.CaptureTs DESC, c.Id DESC
            """);
        Param(cmd, "@id", searchId);

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

    public async Task<SearchSnapshot> SaveSearchAsync(string prefix, string filter, IReadOnlyList<Capture> captures,
        DateTimeOffset cachedAt, CancellationToken ct = default)
    {
        await WriteAsync(() =>
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            long id;
            using (var upsert = Cmd(conn, tx, dialect.Upsert("Searches", ["UrlPrefix", "Filter", "CachedAtUtc"], ["UrlPrefix", "Filter"])))
            {
                Param(upsert, "@UrlPrefix", prefix);
                Param(upsert, "@Filter", filter);
                Param(upsert, "@CachedAtUtc", cachedAt.ToUnixTimeMilliseconds());
                upsert.ExecuteNonQuery();
            }
            using (var find = Cmd(conn, tx, "SELECT Id FROM Searches WHERE UrlPrefix = @p AND Filter = @f"))
            {
                Param(find, "@p", prefix);
                Param(find, "@f", filter);
                id = Convert.ToInt64(find.ExecuteScalar());
            }
            using (var clear = Cmd(conn, tx, "DELETE FROM SearchCaptures WHERE SearchId = @id"))
            {
                Param(clear, "@id", id);
                clear.ExecuteNonQuery();
            }

            using var insertCapture = Cmd(conn, tx, dialect.InsertIgnore("Captures",
                ["CaptureTs", "Original", "MimeType", "StatusCode"], "VALUES (@ts, @o, @m, @s)", ["CaptureTs", "Original"]));
            var cTs = Typed(insertCapture, "@ts", DbType.String);
            var cO = Typed(insertCapture, "@o", DbType.String);
            var cM = Typed(insertCapture, "@m", DbType.String);
            var cS = Typed(insertCapture, "@s", DbType.String);

            using var insertMember = Cmd(conn, tx, dialect.InsertIgnore("SearchCaptures", ["SearchId", "CaptureId"],
                "SELECT @id, Id FROM Captures WHERE CaptureTs = @ts AND Original = @o", ["SearchId", "CaptureId"]));
            Param(insertMember, "@id", id);
            var mTs = Typed(insertMember, "@ts", DbType.String);
            var mO = Typed(insertMember, "@o", DbType.String);

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
        }, ct).ConfigureAwait(false);

        lock (cacheLock) searchCache.Remove(CacheKey(prefix, filter));
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
                conditions.Add("SearchText LIKE '%' || @t" + i + " || '%'");

            using var cmd = Cmd(conn, null, "SELECT CaptureId FROM CaptureSearch WHERE " + string.Join(" AND ", conditions));
            for (var i = 0; i < tokens.Count; i++)
                Param(cmd, "@t" + i, tokens[i]);

            var ids = new List<long>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
            return (IReadOnlyList<long>)ids;
        }, ct).ConfigureAwait(false);
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
                WHERE s.UrlPrefix = @p AND s.Filter = @f
                """);
            Param(cmd, "@p", prefix);
            Param(cmd, "@f", filter);
            using var reader = cmd.ExecuteReader();
            var result = new Dictionary<string, PageMetadata>(StringComparer.Ordinal);
            while (reader.Read())
                result[$"{reader.GetString(0)}|{reader.GetString(1)}"] = ReadMetadata(reader, 2);
            return result;
        }, ct);

    private static PageMetadata ReadMetadata(DbDataReader reader, int o) => new(
        reader.IsDBNull(o) ? null : reader.GetInt64(o),
        reader.IsDBNull(o + 1) ? null : reader.GetInt64(o + 1),
        reader.IsDBNull(o + 2) ? null : reader.GetInt64(o + 2),
        reader.IsDBNull(o + 3) ? null : reader.GetInt64(o + 3),
        reader.IsDBNull(o + 4) ? null : reader.GetInt64(o + 4),
        reader.IsDBNull(o + 5) ? null : reader.GetInt64(o + 5),
        reader.IsDBNull(o + 6) ? null : reader.GetString(o + 6),
        reader.IsDBNull(o + 7) ? null : reader.GetString(o + 7),
        reader.IsDBNull(o + 8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(o + 8)),
        reader.IsDBNull(o + 9) ? null : reader.GetString(o + 9),
        reader.IsDBNull(o + 10) ? 0 : reader.GetDouble(o + 10));

    // ---- pages --------------------------------------------------------------------------------------------

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

                WHERE c.CaptureTs = @ts AND c.Original = @o
                """;
            using var cmd = Cmd(conn, null, sql);
            Param(cmd, "@ts", timestamp);
            Param(cmd, "@o", original);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var capture = new Capture(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            var parsed = new ParsedPage(
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(8) ? null : ReadMetadata(reader, 8));
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

            using var insertCapture = Cmd(conn, tx, dialect.InsertIgnore("Captures",
                ["CaptureTs", "Original", "MimeType", "StatusCode"], "VALUES (@ts, @o, @m, @s)", ["CaptureTs", "Original"]));
            var cTs = Typed(insertCapture, "@ts", DbType.String);
            var cO = Typed(insertCapture, "@o", DbType.String);
            var cM = Typed(insertCapture, "@m", DbType.String);
            var cS = Typed(insertCapture, "@s", DbType.String);

            using var findId = Cmd(conn, tx, "SELECT Id FROM Captures WHERE CaptureTs = @ts AND Original = @o");
            var fTs = Typed(findId, "@ts", DbType.String);
            var fO = Typed(findId, "@o", DbType.String);

            using var insertPage = Cmd(conn, tx, dialect.Upsert("Pages", ["CaptureId", "Title", "Description", "FetchedAtUtc"], ["CaptureId"]));
            var pId = Typed(insertPage, "@CaptureId", DbType.Int64);
            var pT = Typed(insertPage, "@Title", DbType.String);
            var pD = Typed(insertPage, "@Description", DbType.String);
            var pAt = Typed(insertPage, "@FetchedAtUtc", DbType.Int64);

            using var insertText = Cmd(conn, tx, dialect.Upsert("PageText", ["CaptureId", "Body"], ["CaptureId"]));
            var tId = Typed(insertText, "@CaptureId", DbType.Int64);
            var tB = Typed(insertText, "@Body", DbType.String);

            using var upsertMetadata = Cmd(conn, tx, dialect.Upsert("PageMetadata",
                ["CaptureId", "Views", "Likes", "Shares", "Comments", "Score", "EngagementScore", "Source", "Author", "PublishedAtUtc", "Tags", "Confidence"],
                ["CaptureId"]));
            var mId = Typed(upsertMetadata, "@CaptureId", DbType.Int64);
            var mViews = Typed(upsertMetadata, "@Views", DbType.Int64);
            var mLikes = Typed(upsertMetadata, "@Likes", DbType.Int64);
            var mShares = Typed(upsertMetadata, "@Shares", DbType.Int64);
            var mComments = Typed(upsertMetadata, "@Comments", DbType.Int64);
            var mScore = Typed(upsertMetadata, "@Score", DbType.Int64);
            var mEngagement = Typed(upsertMetadata, "@EngagementScore", DbType.Int64);
            var mSource = Typed(upsertMetadata, "@Source", DbType.String);
            var mAuthor = Typed(upsertMetadata, "@Author", DbType.String);
            var mPublished = Typed(upsertMetadata, "@PublishedAtUtc", DbType.Int64);
            var mTags = Typed(upsertMetadata, "@Tags", DbType.String);
            var mConfidence = Typed(upsertMetadata, "@Confidence", DbType.Double);

            using var upsertIndex = Cmd(conn, tx, dialect.Upsert("CaptureSearch", ["CaptureId", "SearchText"], ["CaptureId"]));
            var sId = Typed(upsertIndex, "@CaptureId", DbType.Int64);
            var sText = Typed(upsertIndex, "@SearchText", DbType.String);

            using var insertHtml = Cmd(conn, tx, dialect.Upsert("PageHtml", ["CaptureId", "HtmlGz"], ["CaptureId"]));
            var hId = Typed(insertHtml, "@CaptureId", DbType.Int64);
            var hH = Typed(insertHtml, "@HtmlGz", DbType.Binary);

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
                pT.Value = (object?)Text(page.Parsed.Title) ?? DBNull.Value;
                pD.Value = (object?)Text(page.Parsed.Description) ?? DBNull.Value;
                pAt.Value = page.FetchedAt.ToUnixTimeMilliseconds();
                tB.Value = Text(page.Parsed.Text) ?? "";
                sText.Value = Text(SearchTextMatcher.BuildIndexText(page.Capture.Original, page.Parsed.Title, page.Parsed.Description, page.Parsed.Text)) ?? "";
                var metadata = page.Parsed.Metadata;
                mViews.Value = DbValue(metadata?.Views);
                mLikes.Value = DbValue(metadata?.Likes);
                mShares.Value = DbValue(metadata?.Shares);
                mComments.Value = DbValue(metadata?.Comments);
                mScore.Value = DbValue(metadata?.Score);
                mEngagement.Value = DbValue(metadata?.EngagementScore);
                mSource.Value = (object?)Text(metadata?.Source) ?? DBNull.Value;
                mAuthor.Value = (object?)Text(metadata?.Author) ?? DBNull.Value;
                mPublished.Value = metadata?.PublishedAt?.ToUnixTimeMilliseconds() is long published ? published : DBNull.Value;
                mTags.Value = (object?)Text(metadata?.Tags) ?? DBNull.Value;
                mConfidence.Value = metadata is null ? DBNull.Value : metadata.Confidence;
                htmlFiles.SaveAsync(page.Capture, page.HtmlGz, ct).GetAwaiter().GetResult();
                hH.Value = Array.Empty<byte>();
                insertPage.ExecuteNonQuery();
                insertText.ExecuteNonQuery();
                if (metadata is not null) upsertMetadata.ExecuteNonQuery();
                else
                {
                    using var deleteMetadata = Cmd(conn, tx, "DELETE FROM PageMetadata WHERE CaptureId = @id");
                    Param(deleteMetadata, "@id", id);
                    deleteMetadata.ExecuteNonQuery();
                }
                upsertIndex.ExecuteNonQuery();
                insertHtml.ExecuteNonQuery();
            }

            tx.Commit();
            MarkSaved(newIds);
            return 0;
        }, ct);

    // ---- operations ---------------------------------------------------------------------------------------

    public Task<BackgroundOperation?> FindActiveOperationAsync(string prefix, string filter, OperationKind kind,
        CancellationToken ct = default) => Task.Run(() =>
        {
            using var conn = Open();
            var operation = ReadOne(conn, null, prefix, filter, kind);
            return operation is { IsActive: true } ? operation : null;
        }, ct);

    public Task<BackgroundOperation> CreateOrGetOperationAsync(string prefix, string filter, OperationKind kind,
        DateTimeOffset now, CancellationToken ct = default) => WriteAsync(() =>
        {
            using var conn = Open();
            var existing = ReadOne(conn, null, prefix, filter, kind);
            if (existing is { IsActive: true }) return existing;

            using var cmd = Cmd(conn, null, dialect.Upsert("Operations",
                ["UrlPrefix", "Filter", "Kind", "State", "CreatedAt", "UpdatedAt", "Total", "Completed", "Failed", "Error"],
                ["UrlPrefix", "Filter", "Kind"]));
            var stamp = now.ToUnixTimeMilliseconds();
            Param(cmd, "@UrlPrefix", prefix);
            Param(cmd, "@Filter", filter);
            Param(cmd, "@Kind", (int)kind);
            Param(cmd, "@State", (int)OperationState.Queued);
            Param(cmd, "@CreatedAt", stamp);
            Param(cmd, "@UpdatedAt", stamp);
            Param(cmd, "@Total", 0);
            Param(cmd, "@Completed", 0);
            Param(cmd, "@Failed", 0);
            var error = Typed(cmd, "@Error", DbType.String);
            error.Value = DBNull.Value;
            cmd.ExecuteNonQuery();

            return ReadOne(conn, null, prefix, filter, kind)!;
        }, ct);

    private BackgroundOperation? ReadOne(DbConnection conn, DbTransaction? tx, string prefix, string filter, OperationKind kind)
    {
        using var cmd = Cmd(conn, tx, "SELECT * FROM Operations WHERE UrlPrefix = @p AND Filter = @f AND Kind = @k");
        Param(cmd, "@p", prefix);
        Param(cmd, "@f", filter);
        Param(cmd, "@k", (int)kind);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadOperation(reader) : null;
    }

    public Task<BackgroundOperation?> GetOperationAsync(long id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var conn = Open();
        using var cmd = Cmd(conn, null, "SELECT * FROM Operations WHERE Id = @id");
        Param(cmd, "@id", id);
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
            UPDATE Operations SET State = @state, UpdatedAt = @updated, Total = @total,
                Completed = @completed, Failed = @failed, Error = @error WHERE Id = @id
            """);
        Param(cmd, "@id", operation.Id);
        Param(cmd, "@state", (int)operation.State);
        Param(cmd, "@updated", operation.UpdatedAt.ToUnixTimeMilliseconds());
        Param(cmd, "@total", operation.Total);
        Param(cmd, "@completed", operation.Completed);
        Param(cmd, "@failed", operation.Failed);
        var error = Typed(cmd, "@error", DbType.String);
        error.Value = (object?)Text(operation.Error) ?? DBNull.Value;
        cmd.ExecuteNonQuery();
        return 0;
    }, ct);

    // ---- helpers ------------------------------------------------------------------------------------------

    private async Task<T> WriteAsync<T>(Func<T> work, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(work, ct).ConfigureAwait(false); }
        finally { writeLock.Release(); }
    }

    private DbConnection Open()
    {
        var conn = dialect.CreateConnection(connectionString);
        conn.Open();
        dialect.PrepareConnection(conn);
        return conn;
    }

    private static DbCommand Cmd(DbConnection conn, DbTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    /// A parameter with a value; the .NET type decides the database type.
    private static DbParameter Param(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
        return p;
    }

    /// A reusable parameter with an explicit database type (values are assigned per row).
    private static DbParameter Typed(DbCommand cmd, string name, DbType type)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = type;
        p.Value = DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }

    private static object DbValue(long? value) => value.HasValue ? value.Value : DBNull.Value;

    /// PostgreSQL text cannot contain NUL characters; pages occasionally do.
    private static string? Text(string? value) => value is null ? null : value.Contains('\0') ? value.Replace("\0", "") : value;

    private static BackgroundOperation ReadOperation(DbDataReader reader) => new(
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
