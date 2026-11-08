using Microsoft.Data.Sqlite;
using WaybackViewer.Models;
using WaybackViewer.Parsing;

namespace WaybackViewer.Storage;

// Facet-filtered, paged reads done in SQL. Year / Category / Section / Kind / GroupKey are classified once
// (UrlClassifier is C# code) and persisted on Captures, so a page view reads one page, never the whole list.
public sealed partial class SqliteArchiveStore
{
    private static readonly string[] ClassificationColumns = ["Year", "Category", "Section", "Kind", "GroupKey"];
    private static readonly string[] CategoryOrder = ["Pages", "Images", "Code & styles", "Documents", "Other"];
    private const int ClassifyBatch = 5000;
    private const int MaxMemoFacets = 8;
    private readonly Dictionary<string, (long Version, CaptureFacets Facets)> facetMemo = new(StringComparer.Ordinal);

    private static void EnsureClassificationSchema(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = Cmd(conn, null, "PRAGMA table_info(Captures)"))
        using (var r = info.ExecuteReader())
            while (r.Read()) existing.Add(r.GetString(1));
        foreach (var column in ClassificationColumns.Where(c => !existing.Contains(c)))
        {
            using var alter = Cmd(conn, null, "ALTER TABLE Captures ADD COLUMN " + column + " TEXT");
            alter.ExecuteNonQuery();
        }
        using var index = Cmd(conn, null, "CREATE INDEX IF NOT EXISTS IX_Captures_Unclassified ON Captures (Id) WHERE Kind IS NULL");
        index.ExecuteNonQuery();
    }

    /// Classifies captures saved since the last pass, in bounded batches.
    private static void ClassifyPending(SqliteConnection conn, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<(long Id, Capture Capture)>(ClassifyBatch);
            using (var select = Cmd(conn, null,
                       "SELECT Id, CaptureTs, Original, MimeType, StatusCode FROM Captures WHERE Kind IS NULL LIMIT " + ClassifyBatch))
            using (var r = select.ExecuteReader())
                while (r.Read())
                    rows.Add((r.GetInt64(0), new Capture(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))));
            if (rows.Count == 0) return;

            using var tx = conn.BeginTransaction();
            using var update = Cmd(conn, tx,
                "UPDATE Captures SET Year = $y, Category = $cat, Section = $sec, Kind = $k, GroupKey = $g WHERE Id = $id");
            var pY = update.Parameters.Add("$y", SqliteType.Text);
            var pCat = update.Parameters.Add("$cat", SqliteType.Text);
            var pSec = update.Parameters.Add("$sec", SqliteType.Text);
            var pK = update.Parameters.Add("$k", SqliteType.Text);
            var pG = update.Parameters.Add("$g", SqliteType.Text);
            var pId = update.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (id, capture) in rows)
            {
                var info = UrlClassifier.Classify(capture.Original);
                pY.Value = CaptureFacets.YearOf(capture);
                pCat.Value = CaptureFacets.CategoryOf(capture);
                pSec.Value = info.Section;
                pK.Value = info.Kind;
                pG.Value = info.GroupKey;
                pId.Value = id;
                update.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private async Task EnsureClassifiedAsync(CancellationToken ct)
    {
        var pending = await Task.Run(() =>
        {
            using var conn = Open();
            using var cmd = Cmd(conn, null, "SELECT 1 FROM Captures WHERE Kind IS NULL LIMIT 1");
            return cmd.ExecuteScalar() is not null;
        }, ct).ConfigureAwait(false);
        if (!pending) return;
        await WriteAsync(() =>
        {
            using var conn = Open();
            ClassifyPending(conn, ct);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    private const string RankedColumns = """
        c.Id AS Id, c.CaptureTs AS CaptureTs, c.Original AS Original, c.MimeType AS MimeType, c.StatusCode AS StatusCode,
        c.GroupKey AS GroupKey,
        EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id) AS Rd,
        (lower(c.MimeType) LIKE '%html%') AS Html
        """;

    /// The filtered view as a CTE named v (one row per thread/post/page when Unique, best-ranked first like
    /// CaptureFacets.OnePerGroup: main page over comment links, 200 over redirects/errors, HTML, saved, newest).
    private static string ViewCte(CaptureFilter f, string where) => f.Unique
        ? $"""
            WITH v AS (SELECT Id, CaptureTs, Original, MimeType, StatusCode, GroupKey, Rd, Html FROM (
                SELECT {RankedColumns},
                    ROW_NUMBER() OVER (PARTITION BY c.GroupKey ORDER BY
                        CASE WHEN c.Kind IN ('Thread', 'Page') THEN 0 WHEN c.Kind = 'Comment' THEN 2 ELSE 1 END,
                        CASE WHEN c.StatusCode = '200' THEN 0 WHEN c.StatusCode LIKE '3%' THEN 1 ELSE 2 END,
                        CASE WHEN lower(c.MimeType) LIKE '%html%' THEN 0 ELSE 1 END,
                        CASE WHEN EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id) THEN 0 ELSE 1 END,
                        c.CaptureTs DESC, c.Id DESC) AS rn
                FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId
                WHERE {where}) WHERE rn = 1)
            """
        : $"""
            WITH v AS (SELECT {RankedColumns}
                FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId
                WHERE {where})
            """;

    private static (string Where, List<(string Name, object Value)> Args) FilterSql(long searchId, CaptureFilter f, bool withKind = true,
        string? contentTerm = null)
    {
        var where = "sc.SearchId = $id";
        var args = new List<(string, object)> { ("$id", searchId) };
        void Add(string column, string name, string? value)
        {
            if (value is null) return;
            where += $" AND c.{column} = {name}";
            args.Add((name, value));
        }
        Add("Year", "$year", f.Year);
        Add("Category", "$category", f.Category);
        Add("StatusCode", "$status", f.Status);
        Add("Section", "$section", f.Section);
        if (withKind) Add("Kind", "$kind", f.Kind);
        if (f.Text is not null)
        {
            where += " AND url_match($utext, c.Original) = 1";
            args.Add(("$utext", f.Text));
        }
        if (!string.IsNullOrWhiteSpace(contentTerm))
        {
            // Saved pages only: the CaptureSearch index holds the normalized URL, title, description and text.
            var n = 0;
            var clauses = new List<string>();
            foreach (var variants in SearchTextMatcher.QueryTokenVariants(contentTerm))
            {
                var any = new List<string>();
                foreach (var v in variants)
                {
                    var name = "$ct" + n++;
                    any.Add("cs.SearchText LIKE '%' || " + name + " || '%'");
                    args.Add((name, v));
                }
                clauses.Add("(" + string.Join(" OR ", any) + ")");
            }
            if (clauses.Count > 0)
                where += " AND EXISTS (SELECT 1 FROM CaptureSearch cs WHERE cs.CaptureId = c.Id AND " + string.Join(" AND ", clauses) + ")";
        }
        return (where, args);
    }

    private static void Bind(SqliteCommand cmd, List<(string Name, object Value)> args)
    {
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
    }

    public async Task<FilteredPage?> GetFilteredPageAsync(string prefix, string filter, CaptureFilter f, int page, int pageSize,
        string? contentTerm = null, CancellationToken ct = default)
    {
        await EnsureClassifiedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() => ReadFilteredPage(prefix, filter, f, page, Math.Max(1, pageSize), contentTerm, ct), ct).ConfigureAwait(false);
    }

    // ReadFilteredPage lives in SqliteArchiveStore.Popularity.cs

    /// How many captures each thread/post/page has under the same filters, ignoring the type filter and the
    /// one-per-thread reduction (matches CaptureFacets.GroupSizes), for the groups on the current page only.
    private static Dictionary<string, int> ReadGroupSizes(SqliteConnection conn, long searchId, CaptureFilter f, List<string> keys)
    {
        var (where, args) = FilterSql(searchId, f, withKind: false);
        var names = keys.Select((_, i) => "$g" + i).ToList();
        using var cmd = Cmd(conn, null, $"""
            SELECT c.GroupKey, COUNT(*) FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId
            WHERE {where} AND c.GroupKey IN ({string.Join(",", names)}) GROUP BY c.GroupKey
            """);
        Bind(cmd, args);
        for (var i = 0; i < keys.Count; i++) cmd.Parameters.AddWithValue(names[i], keys[i]);
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) sizes[reader.GetString(0)] = reader.GetInt32(1);
        return sizes;
    }

    /// Facet counts of the whole list (not of the current filter), grouped in SQL and remembered per list version.
    private CaptureFacets ReadFacets(SqliteConnection conn, long searchId, long cachedAtMs, CancellationToken ct)
    {
        var key = searchId + "|" + cachedAtMs;
        long version;
        lock (cacheLock)
        {
            version = savedVersion;
            if (facetMemo.TryGetValue(key, out var memo) && memo.Version == version) return memo.Facets;
        }

        const string from = "FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId WHERE sc.SearchId = $id";
        List<FacetCount> Group(string sql, bool withSaved = false)
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = Cmd(conn, null, sql);
            cmd.Parameters.AddWithValue("$id", searchId);
            var list = new List<FacetCount>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new FacetCount(r.GetString(0), r.GetInt32(1), withSaved ? r.GetInt32(2) : 0));
            return list;
        }

        var years = Group($"""
            SELECT c.Year, COUNT(*), SUM(CASE WHEN EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id) THEN 1 ELSE 0 END)
            {from} GROUP BY c.Year ORDER BY c.Year
            """, withSaved: true);
        var categoryCounts = Group($"SELECT c.Category, COUNT(*) {from} GROUP BY c.Category").ToDictionary(x => x.Value, x => x);
        var categories = CategoryOrder.Where(categoryCounts.ContainsKey).Select(k => categoryCounts[k]).ToList();
        var statuses = Group($"""
            SELECT CASE WHEN trim(c.StatusCode) = '' THEN '-' ELSE c.StatusCode END AS S, COUNT(*)
            {from} GROUP BY S ORDER BY S
            """);
        var sections = Group($"SELECT c.Section, COUNT(*) {from} GROUP BY c.Section ORDER BY COUNT(*) DESC, c.Section LIMIT 12");
        var kindCounts = Group($"SELECT c.Kind, COUNT(*) {from} GROUP BY c.Kind").ToDictionary(x => x.Value, x => x);
        var kinds = UrlClassifier.KindOrder.Where(kindCounts.ContainsKey).Select(k => kindCounts[k]).ToList();

        int uniqueCount;
        using (var cmd = Cmd(conn, null, $"SELECT COUNT(DISTINCT c.GroupKey) {from}"))
        {
            cmd.Parameters.AddWithValue("$id", searchId);
            uniqueCount = Convert.ToInt32(cmd.ExecuteScalar());
        }

        var facets = new CaptureFacets(years, categories, statuses, sections, kinds, uniqueCount);
        lock (cacheLock)
        {
            if (facetMemo.Count >= MaxMemoFacets) facetMemo.Clear();   // bounded
            facetMemo[key] = (version, facets);
        }
        return facets;
    }

    /// Unsaved HTML captures of the filtered view, oldest first, after the cursor; one bounded batch per call.
    public async Task<IReadOnlyList<StoredCapture>> GetUnsavedHtmlBatchAsync(string prefix, string filter, CaptureFilter f,
        StoredCapture? after, int limit, CancellationToken ct = default)
    {
        await EnsureClassifiedAsync(ct).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<StoredCapture>>(() =>
        {
            using var conn = Open();
            long id;
            using (var find = Cmd(conn, null, "SELECT Id FROM Searches WHERE UrlPrefix = $p AND Filter = $f"))
            {
                find.Parameters.AddWithValue("$p", prefix);
                find.Parameters.AddWithValue("$f", filter);
                if (find.ExecuteScalar() is not long found) return [];
                id = found;
            }
            var (where, args) = FilterSql(id, f);
            var cursor = after is null ? "" : " AND (CaptureTs > $ats OR (CaptureTs = $ats AND Id > $aid))";
            using var cmd = Cmd(conn, null, ViewCte(f, where) + $"""
                 SELECT Id, CaptureTs, Original, MimeType, StatusCode FROM v
                 WHERE Html = 1 AND Rd = 0{cursor} ORDER BY CaptureTs ASC, Id ASC LIMIT $limit
                 """);
            Bind(cmd, args);
            cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
            if (after is not null)
            {
                cmd.Parameters.AddWithValue("$ats", after.Capture.Timestamp);
                cmd.Parameters.AddWithValue("$aid", after.Id);
            }
            var rows = new List<StoredCapture>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new StoredCapture(reader.GetInt64(0),
                    new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), false));
            return rows;
        }, ct).ConfigureAwait(false);
    }
}
