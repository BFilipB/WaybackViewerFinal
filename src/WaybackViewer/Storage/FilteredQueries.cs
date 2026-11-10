using System.Data;
using System.Data.Common;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage.Sql;

namespace WaybackViewer.Storage;

/// Remembers facet counts per list version (bounded) so a page view does not regroup the whole list every time.
internal sealed class FacetMemo
{
    private const int MaxEntries = 8;
    private readonly object gate = new();
    private readonly Dictionary<string, (long Version, CaptureFacets Facets)> items = new(StringComparer.Ordinal);

    public bool TryGet(string key, long version, out CaptureFacets facets)
    {
        lock (gate)
        {
            if (items.TryGetValue(key, out var memo) && memo.Version == version) { facets = memo.Facets; return true; }
        }
        facets = null!;
        return false;
    }

    public void Set(string key, long version, CaptureFacets facets)
    {
        lock (gate)
        {
            if (items.Count >= MaxEntries) items.Clear();
            items[key] = (version, facets);
        }
    }
}

/// Facet-filtered, content-searched and popularity-ranked pages computed in SQL, shared by every store. One page of
/// rows is materialized, never the list. Year / Category / Section / Kind / GroupKey are classified once (UrlClassifier
/// is C# code) and persisted on Captures. Standard SQL with "@name" parameters; the few spots where databases differ
/// (GREATEST, truncation, ADD COLUMN) come from the dialect.
internal static class FilteredQueries
{
    private static readonly string[] ClassificationColumns = ["Year", "Category", "Section", "Kind", "GroupKey"];
    private static readonly string[] CategoryOrder = ["Pages", "Images", "Code & styles", "Documents", "Other"];
    private const int ClassifyBatch = 5000;
    private const int HistogramBuckets = 10;

    private const string RankedColumns = """
        c.Id AS Id, c.CaptureTs AS CaptureTs, c.Original AS Original, c.MimeType AS MimeType, c.StatusCode AS StatusCode,
        c.GroupKey AS GroupKey,
        CASE WHEN EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id) THEN 1 ELSE 0 END AS Rd,
        CASE WHEN lower(c.MimeType) LIKE '%html%' THEN 1 ELSE 0 END AS Html
        """;

    // ---- small helpers ----------------------------------------------------------------------------------------

    private static DbCommand Cmd(DbConnection conn, DbTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static DbParameter Param(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
        return p;
    }

    private static DbParameter Typed(DbCommand cmd, string name, DbType type)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = type;
        p.Value = DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }

    private static void Bind(DbCommand cmd, List<(string Name, object Value)> args)
    {
        foreach (var (name, value) in args) Param(cmd, name, value);
    }

    private static int Int(object? value) => Convert.ToInt32(value);

    // ---- classification ---------------------------------------------------------------------------------------

    public static void EnsureClassificationSchema(DbConnection conn, SqlDialect dialect)
    {
        foreach (var column in ClassificationColumns) dialect.EnsureColumn(conn, "Captures", column, "TEXT");
        using var index = Cmd(conn, null, "CREATE INDEX IF NOT EXISTS IX_Captures_Unclassified ON Captures (Id) WHERE Kind IS NULL");
        index.ExecuteNonQuery();
    }

    /// Classifies captures saved since the last pass, in bounded batches.
    public static void ClassifyPending(DbConnection conn, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<(long Id, Capture Capture)>(ClassifyBatch);
            using (var select = Cmd(conn, null,
                       $"SELECT Id, CaptureTs, Original, MimeType, StatusCode FROM Captures WHERE Kind IS NULL ORDER BY Id LIMIT {ClassifyBatch}"))
            using (var r = select.ExecuteReader())
                while (r.Read())
                    rows.Add((r.GetInt64(0), new Capture(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))));
            if (rows.Count == 0) return;

            using var tx = conn.BeginTransaction();
            using var update = Cmd(conn, tx, "UPDATE Captures SET Year = @y, Category = @cat, Section = @sec, Kind = @k, GroupKey = @g WHERE Id = @id");
            var pY = Typed(update, "@y", DbType.String);
            var pCat = Typed(update, "@cat", DbType.String);
            var pSec = Typed(update, "@sec", DbType.String);
            var pK = Typed(update, "@k", DbType.String);
            var pG = Typed(update, "@g", DbType.String);
            var pId = Typed(update, "@id", DbType.Int64);
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

    public static bool HasUnclassified(DbConnection conn)
    {
        using var cmd = Cmd(conn, null, "SELECT 1 FROM Captures WHERE Kind IS NULL LIMIT 1");
        return cmd.ExecuteScalar() is not null;
    }

    // ---- filter SQL -------------------------------------------------------------------------------------------

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
                WHERE {where}) ranked WHERE rn = 1)
            """
        : $"""
            WITH v AS (SELECT {RankedColumns}
                FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId
                WHERE {where})
            """;

    private static (string Where, List<(string Name, object Value)> Args) FilterSql(long searchId, CaptureFilter f, bool withKind = true,
        string? contentTerm = null)
    {
        var where = "sc.SearchId = @id";
        var args = new List<(string, object)> { ("@id", searchId) };
        void Add(string column, string name, string? value)
        {
            if (value is null) return;
            where += $" AND c.{column} = {name}";
            args.Add((name, value));
        }
        Add("Year", "@year", f.Year);
        Add("Category", "@category", f.Category);
        Add("StatusCode", "@status", f.Status);
        Add("Section", "@section", f.Section);
        if (withKind) Add("Kind", "@kind", f.Kind);
        if (f.Text is not null)
        {
            where += " AND url_match(@utext, c.Original) = 1";
            args.Add(("@utext", f.Text));
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
                    var name = "@ct" + n++;
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

    private static long? FindSearch(DbConnection conn, string prefix, string filter, out long cachedAtMs)
    {
        cachedAtMs = 0;
        using var find = Cmd(conn, null, "SELECT Id, CachedAtUtc FROM Searches WHERE UrlPrefix = @p AND Filter = @f");
        Param(find, "@p", prefix);
        Param(find, "@f", filter);
        using var r = find.ExecuteReader();
        if (!r.Read()) return null;
        cachedAtMs = r.GetInt64(1);
        return r.GetInt64(0);
    }

    // ---- popularity signal (per list, persisted) --------------------------------------------------------------

    public static bool SignalsStale(DbConnection conn, string prefix, string filter)
    {
        using var cmd = Cmd(conn, null, """
            SELECT s.CachedAtUtc, g.CachedAtUtc FROM Searches s LEFT JOIN SearchSignals g ON g.SearchId = s.Id
            WHERE s.UrlPrefix = @p AND s.Filter = @f
            """);
        Param(cmd, "@p", prefix);
        Param(cmd, "@f", filter);
        using var r = cmd.ExecuteReader();
        return r.Read() && (r.IsDBNull(1) || r.GetInt64(1) != r.GetInt64(0));
    }

    /// Persists Host + raw archive signal per capture of the list. Streams rows; only the distinct URLs are indexed.
    public static void BuildSignals(Func<DbConnection> open, SqlDialect dialect, string prefix, string filter, CancellationToken ct)
    {
        using var conn = open();
        var id = FindSearch(conn, prefix, filter, out var cachedAt);
        if (id is null) return;

        const string select = "SELECT c.Id, c.Original FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId WHERE sc.SearchId = @id";
        var index = new ArchiveSignals.SignalIndex();
        using var readConn = open();
        using (var pass1 = Cmd(readConn, null, select))
        {
            Param(pass1, "@id", id.Value);
            using var r = pass1.ExecuteReader();
            while (r.Read()) { ct.ThrowIfCancellationRequested(); index.Add(r.GetString(1)); }
        }
        index.Seal();

        using var tx = conn.BeginTransaction();
        using (var clear = Cmd(conn, tx, "DELETE FROM CaptureSignals WHERE SearchId = @id"))
        {
            Param(clear, "@id", id.Value);
            clear.ExecuteNonQuery();
        }
        using var insert = Cmd(conn, tx, "INSERT INTO CaptureSignals (SearchId, CaptureId, Host, Raw) VALUES (@s, @c, @h, @r)");
        Param(insert, "@s", id.Value);
        var pc = Typed(insert, "@c", DbType.Int64);
        var ph = Typed(insert, "@h", DbType.String);
        var pr = Typed(insert, "@r", DbType.Int32);
        var count = 0;
        using (var pass2 = Cmd(readConn, null, select))
        {
            Param(pass2, "@id", id.Value);
            using var r = pass2.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                var original = r.GetString(1);
                pc.Value = r.GetInt64(0);
                ph.Value = ArchiveSignals.HostOf(original);
                pr.Value = index.Raw(original);
                insert.ExecuteNonQuery();
                count++;
            }
        }
        using (var mark = Cmd(conn, tx, dialect.Upsert("SearchSignals", ["SearchId", "CachedAtUtc", "Total"], ["SearchId"])))
        {
            Param(mark, "@SearchId", id.Value);
            Param(mark, "@CachedAtUtc", cachedAt);
            Param(mark, "@Total", (long)count);
            mark.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private const string EffectiveEngagement =
        "COALESCE(mE, COALESCE(mS,0) + COALESCE(mV,0) + COALESCE(mL,0)*3 + COALESCE(mC,0)*4 + COALESCE(mSh,0)*2)";

    /// 1 when the sorted single metric (views/likes/comments/score) is known for the row. Unknown is not zero (S7): such rows
    /// rank after every row that has the value, for both most and least. Combined metrics always count as known.
    private static string HasMetricSql(string metric) => metric switch
    {
        "views" or "viewed" => "CASE WHEN HasM = 1 AND mV IS NOT NULL THEN 2 WHEN HasM = 1 AND (mL IS NOT NULL OR mC IS NOT NULL OR mS IS NOT NULL OR mSh IS NOT NULL) THEN 1 ELSE 0 END",
        "likes" or "liked" => "CASE WHEN HasM = 1 AND mL IS NOT NULL THEN 1 ELSE 0 END",
        "comments" or "commented" => "CASE WHEN HasM = 1 AND mC IS NOT NULL THEN 1 ELSE 0 END",
        "score" => "CASE WHEN HasM = 1 AND mS IS NOT NULL THEN 1 ELSE 0 END",
        _ => "1"
    };

    /// Views sort value: real views when known; otherwise the engagement score as an estimated proxy (tier Hv = 1, never mixed with real views).
    private const string ViewsSortSql = "CASE WHEN HasM = 1 THEN CASE WHEN mV IS NOT NULL THEN mV ELSE " + EffectiveEngagement + " END ELSE 0 END";

    private static string MetricSql(SqlDialect d, string metric, bool hasMetadata) => hasMetadata
        ? metric switch
        {
            "views" or "viewed" => "COALESCE(mV,0)",
            "likes" or "liked" => "COALESCE(mL,0)",
            "comments" or "commented" => "COALESCE(mC,0)",
            "score" => "COALESCE(mS,0)",
            "engaged" => EffectiveEngagement,
            _ => d.Greatest("COALESCE(mV,0) + COALESCE(mL,0) + COALESCE(mC,0) + COALESCE(mSh,0) + COALESCE(mS,0)", EffectiveEngagement)
        }
        : metric switch
        {
            "views" or "viewed" or "likes" or "liked" or "comments" or "commented" or "score" => "0",
            _ => "Sg"
        };

    /// CTEs bs/dist/cum (site percentile of the archive signal), mm (view + metadata + Sg) and pm (adds Pm = threshold
    /// metric, Ps = sort metric). Saved page metadata wins; otherwise the archive signal, ranked within each site.
    private static string PopularityCtes(SqlDialect d, PopularityQuery p)
    {
        var pm = p.Metric == "none" ? "0" : $"CASE WHEN HasM = 1 THEN {MetricSql(d, p.Metric, true)} ELSE {MetricSql(d, p.Metric, false)} END";
        var ps = p.Sort is "newest" or "published" ? "0" : p.SortMetric is "views" or "viewed" ? ViewsSortSql : $"CASE WHEN HasM = 1 THEN {MetricSql(d, p.SortMetric, true)} ELSE {MetricSql(d, p.SortMetric, false)} END";
        var hv = p.Sort is "newest" or "published" ? "1" : HasMetricSql(p.SortMetric);
        var score = d.TruncToInt($"99.0 * ((cum.lo + cum.hi) / 2.0) / {d.Greatest("cum.n", "1")}");
        return $"""
            , bs AS (SELECT sg.CaptureId, sg.Host, sg.Raw FROM CaptureSignals sg
                WHERE sg.SearchId = @id AND NOT EXISTS (SELECT 1 FROM PageMetadata m WHERE m.CaptureId = sg.CaptureId)),
            dist AS (SELECT Host, Raw, COUNT(*) AS cnt FROM bs GROUP BY Host, Raw),
            cum AS (SELECT Host, Raw,
                SUM(cnt) OVER (PARTITION BY Host ORDER BY Raw) - cnt AS lo,
                SUM(cnt) OVER (PARTITION BY Host ORDER BY Raw) AS hi,
                SUM(cnt) OVER (PARTITION BY Host) AS n FROM dist),
            mm AS (SELECT v.Id, v.CaptureTs, v.Original, v.MimeType, v.StatusCode, v.GroupKey, v.Rd, v.Html,
                m.Views AS mV, m.Likes AS mL, m.Shares AS mSh, m.Comments AS mC, m.Score AS mS, m.EngagementScore AS mE,
                m.Source AS mSource, m.Author AS mAuthor, m.PublishedAtUtc AS mPub, m.Tags AS mTags, m.Confidence AS mConf,
                CASE WHEN m.CaptureId IS NOT NULL THEN 1 ELSE 0 END AS HasM,
                CASE WHEN m.CaptureId IS NULL AND cum.n IS NOT NULL THEN 1 + {score} ELSE 0 END AS Sg
                FROM v
                LEFT JOIN PageMetadata m ON m.CaptureId = v.Id
                LEFT JOIN CaptureSignals sg ON sg.SearchId = @id AND sg.CaptureId = v.Id
                LEFT JOIN cum ON cum.Host = sg.Host AND cum.Raw = sg.Raw),
            pm0 AS (SELECT mm.*, {pm} AS Pm, {ps} AS Ps, ({ps}) * CASE WHEN HasM THEN 0.5 + 0.5 * COALESCE(mConf, 0) ELSE 1.0 END AS Wk,
                CASE WHEN HasM = 0 THEN 0 WHEN mConf >= 0.5 THEN 2 WHEN mConf >= 0.15 THEN 1 ELSE 0 END AS Tr,
                {hv} AS Hv FROM mm),
            pm AS (SELECT pm0.*, CUME_DIST() OVER (PARTITION BY Hv, Tr ORDER BY Ps) * CASE WHEN HasM THEN 0.5 + 0.5 * COALESCE(mConf, 0) ELSE 1.0 END AS Pk FROM pm0)
            """;
    }

    // ---- the page ---------------------------------------------------------------------------------------------

    public static FilteredPage? ReadPage(DbConnection conn, SqlDialect d, FacetMemo memo, long version, string prefix, string filter,
        CaptureFilter f, int page, int pageSize, string? contentTerm, PopularityQuery? pop, CancellationToken ct)
    {
        if (FindSearch(conn, prefix, filter, out var cachedAtMs) is not { } id) return null;

        var (where, args) = FilterSql(id, f, contentTerm: contentTerm);
        var cte = ViewCte(f, where);
        var from = "v";
        var order = "CaptureTs DESC, Id DESC";
        var kept = "";
        if (pop is not null)
        {
            cte += PopularityCtes(d, pop);
            from = "pm";
            if (pop.Sort == "published") order = "CASE WHEN mPub IS NULL THEN 1 ELSE 0 END, mPub DESC, CaptureTs DESC, Id DESC";
            else if (pop.Sort != "newest") order = pop.Least ? "Hv DESC, Tr DESC, Pk ASC, Wk ASC, CaptureTs DESC, Id DESC" : "Hv DESC, Tr DESC, Pk DESC, Wk DESC, CaptureTs DESC, Id DESC";   // measured > estimated > archive signal
            if (pop.Metric != "none" && (pop.Threshold > 0 || pop.MaxThreshold > 0))
            {
                kept = $" WHERE Pm >= @thr AND Tr >= {pop.MinTier}" + (pop.MaxThreshold > 0 ? " AND Pm <= @max" : "");
                args.Add(("@thr", (long)pop.Threshold));
                if (pop.MaxThreshold > 0) args.Add(("@max", (long)pop.MaxThreshold));
            }
        }

        int everything, total;
        using (var count = Cmd(conn, null, cte + $" SELECT COUNT(*) FROM {from}"))
        {
            Bind(count, args);
            everything = Int(count.ExecuteScalar());
        }
        if (kept.Length == 0) total = everything;
        else
            using (var count = Cmd(conn, null, cte + $" SELECT COUNT(*) FROM {from}{kept}"))
            {
                Bind(count, args);
                total = Int(count.ExecuteScalar());
            }
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);

        var rows = new List<StoredCapture>(pageSize);
        var groups = new List<string>();
        Dictionary<string, PageMetadata>? popularity = pop is null ? null : new(StringComparer.Ordinal);
        ct.ThrowIfCancellationRequested();
        var extra = pop is null ? "" : ", mV, mL, mSh, mC, mS, mE, mSource, mAuthor, mPub, mTags, mConf, HasM, Sg";
        using (var cmd = Cmd(conn, null, cte + $" SELECT Id, CaptureTs, Original, MimeType, StatusCode, Rd, GroupKey{extra} FROM {from}{kept} " +
                                         $"ORDER BY {order} LIMIT @limit OFFSET @offset"))
        {
            Bind(cmd, args);
            Param(cmd, "@limit", pageSize);
            Param(cmd, "@offset", (page - 1) * pageSize);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var capture = new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
                rows.Add(new StoredCapture(reader.GetInt64(0), capture, Convert.ToInt64(reader.GetValue(5)) != 0));
                groups.Add(reader.IsDBNull(6) ? "" : reader.GetString(6));
                if (popularity is null) continue;
                long? L(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
                if (Convert.ToInt64(reader.GetValue(18)) != 0)
                    popularity[capture.Key] = new PageMetadata(L(7), L(8), L(9), L(10), L(11), L(12),
                        reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                        reader.IsDBNull(15) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(15)),
                        reader.IsDBNull(16) ? null : reader.GetString(16), reader.IsDBNull(17) ? 0 : reader.GetDouble(17));
                else if (Convert.ToInt64(reader.GetValue(19)) > 0)
                    popularity[capture.Key] = new PageMetadata(EngagementScore: Convert.ToInt64(reader.GetValue(19)), Confidence: 0.1);
            }
        }

        List<PopularityPreset>? presets = null;
        List<int>? histogram = null;
        int coverageMeasured = 0, coverageTotal = 0; Dictionary<string, int>? metricCoverage = null;
        if (pop is not null && pop.Metric != "none")
        {
            presets = [];
            using (var cov = Cmd(conn, null, cte + " SELECT COUNT(*), COALESCE(SUM(CASE WHEN Tr >= 2 THEN 1 ELSE 0 END), 0), COALESCE(SUM(CASE WHEN Tr >= 2 AND mV IS NOT NULL THEN 1 ELSE 0 END), 0), COALESCE(SUM(CASE WHEN Tr >= 2 AND mL IS NOT NULL THEN 1 ELSE 0 END), 0), COALESCE(SUM(CASE WHEN Tr >= 2 AND mC IS NOT NULL THEN 1 ELSE 0 END), 0), COALESCE(SUM(CASE WHEN Tr >= 2 AND mS IS NOT NULL THEN 1 ELSE 0 END), 0) FROM pm"))
            {
                Bind(cov, args);
                using var cr = cov.ExecuteReader();
                if (cr.Read()) { coverageTotal = Convert.ToInt32(cr.GetValue(0)); coverageMeasured = Convert.ToInt32(cr.GetValue(1)); metricCoverage = new() { ["views"] = Convert.ToInt32(cr.GetValue(2)), ["likes"] = Convert.ToInt32(cr.GetValue(3)), ["comments"] = Convert.ToInt32(cr.GetValue(4)), ["score"] = Convert.ToInt32(cr.GetValue(5)) }; }
            }
            int eligible;
            using (var el = Cmd(conn, null, cte + $" SELECT COUNT(*) FROM pm WHERE Tr >= {pop.MinTier}"))
            {
                Bind(el, args);
                eligible = Convert.ToInt32(el.ExecuteScalar());
            }
            if (eligible > 0)
                foreach (var percent in new[] { 10, 25, 50 })
                {
                    var index = Math.Clamp((int)Math.Ceiling(eligible * percent / 100.0) - 1, 0, eligible - 1);
                    using var preset = Cmd(conn, null, cte + $" SELECT Pm FROM pm WHERE Tr >= {pop.MinTier} ORDER BY Pm DESC LIMIT 1 OFFSET @o");
                    Bind(preset, args);
                    Param(preset, "@o", index);
                    var value = Convert.ToInt64(preset.ExecuteScalar());
                    if (value > 0 && (presets.Count == 0 || presets[^1].Value != value))
                        presets.Add(new PopularityPreset(percent, (int)Math.Min(value, int.MaxValue)));
                }

            var bins = new int[HistogramBuckets];
            if (eligible > 0)
            {
                long max;
                using (var maxCmd = Cmd(conn, null, cte + $" SELECT COALESCE(MAX(Pm), 0) FROM pm WHERE Tr >= {pop.MinTier}"))
                {
                    Bind(maxCmd, args);
                    max = Convert.ToInt64(maxCmd.ExecuteScalar());
                }
                var bucket = d.Least((HistogramBuckets - 1).ToString(), $"Pm * {HistogramBuckets} / (@max + 1)");
                using var hist = Cmd(conn, null, cte + $" SELECT CASE WHEN @max <= 0 THEN 0 ELSE {bucket} END AS b, COUNT(*) FROM pm WHERE Tr >= {pop.MinTier} GROUP BY CASE WHEN @max <= 0 THEN 0 ELSE {bucket} END");
                Bind(hist, args);
                Param(hist, "@max", max);
                using var reader = hist.ExecuteReader();
                while (reader.Read()) bins[Int(reader.GetValue(0))] += Int(reader.GetValue(1));
            }
            histogram = [.. bins];
        }

        int unsavedInView;
        using (var unsaved = Cmd(conn, null, cte + $" SELECT COUNT(*) FROM {from} WHERE Html = 1 AND Rd = 0"))
        {
            Bind(unsaved, args);
            unsavedInView = Int(unsaved.ExecuteScalar());
        }
        int unsavedTotal, htmlTotal;
        using (var all = Cmd(conn, null, "SELECT COUNT(*) FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId WHERE sc.SearchId = @id AND lower(c.MimeType) LIKE '%html%'"))
        {
            Param(all, "@id", id);
            htmlTotal = Int(all.ExecuteScalar());
        }
        using (var unsaved = Cmd(conn, null, """
                 SELECT COUNT(*) FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId
                 WHERE sc.SearchId = @id AND lower(c.MimeType) LIKE '%html%'
                   AND NOT EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id)
                 """))
        {
            Param(unsaved, "@id", id);
            unsavedTotal = Int(unsaved.ExecuteScalar());
        }

        Dictionary<string, int>? groupSizes = null;
        if (f.Unique && groups.Count > 0)
            groupSizes = ReadGroupSizes(conn, id, f, groups.Distinct(StringComparer.Ordinal).ToList());

        var cachedAt = DateTimeOffset.FromUnixTimeMilliseconds(cachedAtMs);
        return new FilteredPage(id, cachedAt, page, total, rows, ReadFacets(conn, memo, version, id, cachedAtMs, ct), groupSizes, htmlTotal,
            unsavedTotal, unsavedInView, popularity, everything - total, presets, histogram, pop is null ? 0 : unsavedInView, coverageMeasured, coverageTotal, metricCoverage);
    }

    /// How many captures each thread/post/page has under the same filters, ignoring the type filter and the
    /// one-per-thread reduction (matches CaptureFacets.GroupSizes), for the groups on the current page only.
    private static Dictionary<string, int> ReadGroupSizes(DbConnection conn, long searchId, CaptureFilter f, List<string> keys)
    {
        var (where, args) = FilterSql(searchId, f, withKind: false);
        var names = keys.Select((_, i) => "@g" + i).ToList();
        using var cmd = Cmd(conn, null, $"""
            SELECT c.GroupKey, COUNT(*) FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId
            WHERE {where} AND c.GroupKey IN ({string.Join(",", names)}) GROUP BY c.GroupKey
            """);
        Bind(cmd, args);
        for (var i = 0; i < keys.Count; i++) Param(cmd, names[i], keys[i]);
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) sizes[reader.GetString(0)] = Int(reader.GetValue(1));
        return sizes;
    }

    /// Facet counts of the whole list (not of the current filter), grouped in SQL and remembered per list version.
    private static CaptureFacets ReadFacets(DbConnection conn, FacetMemo memo, long version, long searchId, long cachedAtMs, CancellationToken ct)
    {
        var key = searchId + "|" + cachedAtMs;
        if (memo.TryGet(key, version, out var known)) return known;

        const string from = "FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId WHERE sc.SearchId = @id";
        List<FacetCount> Group(string sql, bool withSaved = false)
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = Cmd(conn, null, sql);
            Param(cmd, "@id", searchId);
            var list = new List<FacetCount>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new FacetCount(r.GetString(0), Int(r.GetValue(1)), withSaved ? Int(r.GetValue(2)) : 0));
            return list;
        }

        var years = Group($"""
            SELECT c.Year, COUNT(*), SUM(CASE WHEN EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id) THEN 1 ELSE 0 END)
            {from} GROUP BY c.Year ORDER BY c.Year
            """, withSaved: true);
        var categoryCounts = Group($"SELECT c.Category, COUNT(*) {from} GROUP BY c.Category").ToDictionary(x => x.Value, x => x);
        var categories = CategoryOrder.Where(categoryCounts.ContainsKey).Select(k => categoryCounts[k]).ToList();
        var statuses = Group($"""
            SELECT CASE WHEN trim(c.StatusCode) = '' THEN '-' ELSE c.StatusCode END, COUNT(*)
            {from} GROUP BY CASE WHEN trim(c.StatusCode) = '' THEN '-' ELSE c.StatusCode END
            ORDER BY CASE WHEN trim(c.StatusCode) = '' THEN '-' ELSE c.StatusCode END
            """);
        var sections = Group($"SELECT c.Section, COUNT(*) {from} GROUP BY c.Section ORDER BY COUNT(*) DESC, c.Section LIMIT 12");
        var kindCounts = Group($"SELECT c.Kind, COUNT(*) {from} GROUP BY c.Kind").ToDictionary(x => x.Value, x => x);
        var kinds = UrlClassifier.KindOrder.Where(kindCounts.ContainsKey).Select(k => kindCounts[k]).ToList();

        int uniqueCount;
        using (var cmd = Cmd(conn, null, $"SELECT COUNT(DISTINCT c.GroupKey) {from}"))
        {
            Param(cmd, "@id", searchId);
            uniqueCount = Int(cmd.ExecuteScalar());
        }

        var facets = new CaptureFacets(years, categories, statuses, sections, kinds, uniqueCount);
        memo.Set(key, version, facets);
        return facets;
    }

	/// Newest captures of one URL inside a list, bounded by limit.
	public static IReadOnlyList<StoredCapture>? ReadHistory(DbConnection conn, string prefix, string filter, string original, int limit)
	{
		if (FindSearch(conn, prefix, filter, out _) is not { } id) return null;
		using var cmd = Cmd(conn, null, """
			SELECT c.Id, c.CaptureTs, c.Original, c.MimeType, c.StatusCode,
				   CASE WHEN EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id) THEN 1 ELSE 0 END
			FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId
			WHERE sc.SearchId = @id AND c.Original = @o
			ORDER BY c.CaptureTs DESC, c.Id DESC LIMIT @limit
			""");
		Param(cmd, "@id", id);
		Param(cmd, "@o", original);
		Param(cmd, "@limit", Math.Max(1, limit));
		var rows = new List<StoredCapture>();
		using var reader = cmd.ExecuteReader();
		while (reader.Read())
			rows.Add(new StoredCapture(reader.GetInt64(0),
				new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)),
				Convert.ToInt64(reader.GetValue(5)) != 0));
		return rows;
	}

	/// Unsaved HTML captures of the filtered view, oldest first, after the cursor; one bounded batch per call.
    public static IReadOnlyList<StoredCapture> ReadUnsavedBatch(DbConnection conn, string prefix, string filter, CaptureFilter f,
        StoredCapture? after, int limit)
    {
        if (FindSearch(conn, prefix, filter, out _) is not { } id) return [];
        var (where, args) = FilterSql(id, f);
        var cursor = after is null ? "" : " AND (CaptureTs > @ats OR (CaptureTs = @ats AND Id > @aid))";
        using var cmd = Cmd(conn, null, ViewCte(f, where) + $"""
             SELECT Id, CaptureTs, Original, MimeType, StatusCode FROM v
             WHERE Html = 1 AND Rd = 0{cursor} ORDER BY CaptureTs ASC, Id ASC LIMIT @limit
             """);
        Bind(cmd, args);
        Param(cmd, "@limit", Math.Max(1, limit));
        if (after is not null)
        {
            Param(cmd, "@ats", after.Capture.Timestamp);
            Param(cmd, "@aid", after.Id);
        }
        var rows = new List<StoredCapture>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new StoredCapture(reader.GetInt64(0),
                new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), false));
        return rows;
    }
}
