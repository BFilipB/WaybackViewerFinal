using Microsoft.Data.Sqlite;
using WaybackViewer.Models;
using WaybackViewer.Parsing;

namespace WaybackViewer.Storage;

// Facet-filtered pages (optionally ranked / thresholded by popularity) read in SQL: one page of rows, counts,
// facets, presets and a histogram, never the whole list. Popularity mirrors the in-memory rules: saved page
// metadata when present, otherwise the archive signal (ArchiveSignals), percentile-ranked within each site.
public sealed partial class SqliteArchiveStore
{
    private const int HistogramBuckets = 10;
    private const string EffectiveEngagement =
        "COALESCE(mE, COALESCE(mS,0) + COALESCE(mV,0) + COALESCE(mL,0)*3 + COALESCE(mC,0)*4 + COALESCE(mSh,0)*2)";

    public async Task<FilteredPage?> GetPopularPageAsync(string prefix, string filter, CaptureFilter f, int page, int pageSize,
        string? contentTerm, PopularityQuery popularity, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
		await EnsureClassifiedAsync(ct).ConfigureAwait(false);
		await EnsureSignalsAsync(prefix, filter, ct).ConfigureAwait(false);
		var ensureMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
		var result = await Task.Run(() => ReadFilteredPage(prefix, filter, f, page, Math.Max(1, pageSize), contentTerm, ct, popularity), ct)
			.ConfigureAwait(false);
		Log?.LogInformation("store.read_page kind=popular sort={Sort} metric={Metric} ensureMs={Ensure:F0} readMs={Read:F0} total={Total}",
			popularity.Sort, popularity.Metric, ensureMs, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds - ensureMs, result?.Total);
		return result;
    }

	public Task<IReadOnlyList<StoredCapture>?> GetUrlHistoryAsync(string prefix, string filter, string original, int limit,
		CancellationToken ct = default) =>
		Task.Run(() =>
		{
			using var conn = Open();
			using var cmd = Cmd(conn, null, "SELECT Id FROM Searches WHERE UrlPrefix = $p AND Filter = $f");
			cmd.Parameters.AddWithValue("$p", prefix);
			cmd.Parameters.AddWithValue("$f", filter);
			if (cmd.ExecuteScalar() is not long id) return (IReadOnlyList<StoredCapture>?)null;
			using var q = Cmd(conn, null, """
				SELECT c.Id, c.CaptureTs, c.Original, c.MimeType, c.StatusCode,
					   EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id)
				FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId
				WHERE sc.SearchId = $id AND c.Original = $o
				ORDER BY c.CaptureTs DESC, c.Id DESC LIMIT $limit
				""");
			q.Parameters.AddWithValue("$id", id);
			q.Parameters.AddWithValue("$o", original);
			q.Parameters.AddWithValue("$limit", Math.Max(1, limit));
			var rows = new List<StoredCapture>();
			using var reader = q.ExecuteReader();
			while (reader.Read())
				rows.Add(new StoredCapture(reader.GetInt64(0),
					new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), reader.GetInt64(5) != 0));
			return rows;
		}, ct);

	public Task<IReadOnlyList<StoredCapture>> GetPagesMissingPublishedAsync(string prefix, string filter, long afterId, int limit,
		CancellationToken ct = default) =>
		Task.Run(() =>
		{
			using var conn = Open();
			using var cmd = Cmd(conn, null, """
				SELECT c.Id, c.CaptureTs, c.Original, c.MimeType, c.StatusCode
				FROM Searches s JOIN SearchCaptures sc ON sc.SearchId = s.Id JOIN Captures c ON c.Id = sc.CaptureId
				WHERE s.UrlPrefix = $p AND s.Filter = $f AND c.Id > $after
				  AND EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id)
				  AND NOT EXISTS (SELECT 1 FROM PageMetadata m WHERE m.CaptureId = c.Id AND m.PublishedAtUtc IS NOT NULL)
				ORDER BY c.Id LIMIT $limit
				""");
			cmd.Parameters.AddWithValue("$p", prefix);
			cmd.Parameters.AddWithValue("$f", filter);
			cmd.Parameters.AddWithValue("$after", afterId);
			cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
			var rows = new List<StoredCapture>();
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
				rows.Add(new StoredCapture(reader.GetInt64(0),
					new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), true));
			return (IReadOnlyList<StoredCapture>)rows;
		}, ct);

/// Rewrites only the PageMetadata rows (no payload, text or search-index writes), one transaction for the whole batch.
public Task UpdateMetadataAsync(IReadOnlyList<(Capture Capture, PageMetadata Metadata)> items, CancellationToken ct = default) =>
Task.Run(() =>
{
if (items.Count == 0) return;
using var conn = Open();
using var tx = conn.BeginTransaction();
using var find = Cmd(conn, tx, "SELECT Id FROM Captures WHERE CaptureTs = $ts AND Original = $o");
var fTs = find.Parameters.Add("$ts", SqliteType.Text);
var fO = find.Parameters.Add("$o", SqliteType.Text);
using var upsert = Cmd(conn, tx, "INSERT OR REPLACE INTO PageMetadata (CaptureId, Views, Likes, Shares, Comments, Score, EngagementScore, Source, Author, PublishedAtUtc, Tags, Confidence, ExtractorVersion) VALUES ($id, $views, $likes, $shares, $comments, $score, $engagement, $source, $author, $published, $tags, $confidence, $ev)");
var pId = upsert.Parameters.Add("$id", SqliteType.Integer);
var pViews = upsert.Parameters.Add("$views", SqliteType.Integer);
var pLikes = upsert.Parameters.Add("$likes", SqliteType.Integer);
var pShares = upsert.Parameters.Add("$shares", SqliteType.Integer);
var pComments = upsert.Parameters.Add("$comments", SqliteType.Integer);
var pScore = upsert.Parameters.Add("$score", SqliteType.Integer);
var pEngagement = upsert.Parameters.Add("$engagement", SqliteType.Integer);
var pSource = upsert.Parameters.Add("$source", SqliteType.Text);
var pAuthor = upsert.Parameters.Add("$author", SqliteType.Text);
var pPublished = upsert.Parameters.Add("$published", SqliteType.Integer);
var pTags = upsert.Parameters.Add("$tags", SqliteType.Text);
var pConfidence = upsert.Parameters.Add("$confidence", SqliteType.Real);
var pVersion = upsert.Parameters.Add("$ev", SqliteType.Integer);
pVersion.Value = ExtractorVersion.Current;
foreach (var (capture, m) in items)
{
ct.ThrowIfCancellationRequested();
fTs.Value = capture.Timestamp;
fO.Value = capture.Original;
if (find.ExecuteScalar() is not long id) continue;   // capture no longer stored: nothing to update
pId.Value = id;
pViews.Value = DbValue(m.Views);
pLikes.Value = DbValue(m.Likes);
pShares.Value = DbValue(m.Shares);
pComments.Value = DbValue(m.Comments);
pScore.Value = DbValue(m.Score);
pEngagement.Value = DbValue(m.EngagementScore);
pSource.Value = (object?)m.Source ?? DBNull.Value;
pAuthor.Value = (object?)m.Author ?? DBNull.Value;
pPublished.Value = m.PublishedAt?.ToUnixTimeMilliseconds() is long p ? p : DBNull.Value;
pTags.Value = (object?)m.Tags ?? DBNull.Value;
pConfidence.Value = m.Confidence;
upsert.ExecuteNonQuery();
}
tx.Commit();
}, ct);
	public Task<IReadOnlyList<StoredCapture>> GetPagesWithStaleExtractorAsync(string prefix, string filter, int currentVersion, long afterId, int limit,
		CancellationToken ct = default) =>
		Task.Run(() =>
		{
			using var conn = Open();
			using var cmd = Cmd(conn, null, """
				SELECT c.Id, c.CaptureTs, c.Original, c.MimeType, c.StatusCode
				FROM Searches s JOIN SearchCaptures sc ON sc.SearchId = s.Id JOIN Captures c ON c.Id = sc.CaptureId
				JOIN PageMetadata m ON m.CaptureId = c.Id
				WHERE s.UrlPrefix = $p AND s.Filter = $f AND c.Id > $after
				  AND (m.ExtractorVersion IS NULL OR m.ExtractorVersion < $v)
				ORDER BY c.Id LIMIT $limit
				""");
			cmd.Parameters.AddWithValue("$p", prefix);
			cmd.Parameters.AddWithValue("$f", filter);
			cmd.Parameters.AddWithValue("$after", afterId);
			cmd.Parameters.AddWithValue("$v", currentVersion);
			cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
			var rows = new List<StoredCapture>();
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
				rows.Add(new StoredCapture(reader.GetInt64(0),
					new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), true));
			return (IReadOnlyList<StoredCapture>)rows;
		}, ct);

	private async Task EnsureSignalsAsync(string prefix, string filter, CancellationToken ct)
    {
        var stale = await Task.Run(() =>
        {
            using var conn = Open();
            using var cmd = Cmd(conn, null, """
                SELECT s.CachedAtUtc, g.CachedAtUtc FROM Searches s LEFT JOIN SearchSignals g ON g.SearchId = s.Id
                WHERE s.UrlPrefix = $p AND s.Filter = $f
                """);
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$f", filter);
            using var r = cmd.ExecuteReader();
            return r.Read() && (r.IsDBNull(1) || r.GetInt64(1) != r.GetInt64(0));
        }, ct).ConfigureAwait(false);
        if (!stale) return;
        await WriteAsync(() =>
        {
            BuildSignals(prefix, filter, ct);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// Persists Host + raw archive signal per capture of the list. Streams rows; only the distinct URLs are indexed.
    private void BuildSignals(string prefix, string filter, CancellationToken ct)
    {
        using var conn = Open();
        long id, cachedAt;
        using (var find = Cmd(conn, null, "SELECT Id, CachedAtUtc FROM Searches WHERE UrlPrefix = $p AND Filter = $f"))
        {
            find.Parameters.AddWithValue("$p", prefix);
            find.Parameters.AddWithValue("$f", filter);
            using var r = find.ExecuteReader();
            if (!r.Read()) return;
            id = r.GetInt64(0);
            cachedAt = r.GetInt64(1);
        }

        const string select = "SELECT c.Id, c.Original FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId WHERE sc.SearchId = $id";
        var index = new ArchiveSignals.SignalIndex();
        using (var readConn = Open())
        {
            using (var pass1 = Cmd(readConn, null, select))
            {
                pass1.Parameters.AddWithValue("$id", id);
                using var r = pass1.ExecuteReader();
                while (r.Read()) { ct.ThrowIfCancellationRequested(); index.Add(r.GetString(1)); }
            }
            index.Seal();

            using var tx = conn.BeginTransaction();
            using (var clear = Cmd(conn, tx, "DELETE FROM CaptureSignals WHERE SearchId = $id"))
            {
                clear.Parameters.AddWithValue("$id", id);
                clear.ExecuteNonQuery();
            }
            using var insert = Cmd(conn, tx, "INSERT OR REPLACE INTO CaptureSignals (SearchId, CaptureId, Host, Raw) VALUES ($s, $c, $h, $r)");
            insert.Parameters.AddWithValue("$s", id);
            var pc = insert.Parameters.Add("$c", SqliteType.Integer);
            var ph = insert.Parameters.Add("$h", SqliteType.Text);
            var pr = insert.Parameters.Add("$r", SqliteType.Integer);
            var count = 0;
            using (var pass2 = Cmd(readConn, null, select))
            {
                pass2.Parameters.AddWithValue("$id", id);
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
            using (var mark = Cmd(conn, tx, "INSERT OR REPLACE INTO SearchSignals (SearchId, CachedAtUtc, Total) VALUES ($s, $t, $n)"))
            {
                mark.Parameters.AddWithValue("$s", id);
                mark.Parameters.AddWithValue("$t", cachedAt);
                mark.Parameters.AddWithValue("$n", count);
                mark.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// 1 when the sorted single metric (views/likes/comments/score) is known for the row. Unknown is not zero (S7): such rows
    /// rank after every row that has the value, for both most and least. Combined metrics always count as known.
    private static string HasMetricSql(string metric) => metric switch
    {
        "views" or "viewed" => "CASE WHEN HasM AND mV IS NOT NULL THEN 2 WHEN HasM AND (mL IS NOT NULL OR mC IS NOT NULL OR mS IS NOT NULL OR mSh IS NOT NULL) THEN 1 ELSE 0 END",
        "likes" or "liked" => "(HasM AND mL IS NOT NULL)",
        "comments" or "commented" => "(HasM AND mC IS NOT NULL)",
        "score" => "(HasM AND mS IS NOT NULL)",
        _ => "1"
    };

    /// Views sort value: real views when known; otherwise the engagement score as an estimated proxy (tier Hv = 1, never mixed with real views).
    private const string ViewsSortSql = "CASE WHEN HasM THEN CASE WHEN mV IS NOT NULL THEN mV ELSE " + EffectiveEngagement + " END ELSE 0 END";

    private static string MetricSql(string metric, bool hasMetadata) => hasMetadata
        ? metric switch
        {
            "views" or "viewed" => "COALESCE(mV,0)",
            "likes" or "liked" => "COALESCE(mL,0)",
            "comments" or "commented" => "COALESCE(mC,0)",
            "score" => "COALESCE(mS,0)",
            "engaged" => EffectiveEngagement,
            _ => $"MAX(COALESCE(mV,0) + COALESCE(mL,0) + COALESCE(mC,0) + COALESCE(mSh,0) + COALESCE(mS,0), {EffectiveEngagement})"
        }
        : metric switch
        {
            "views" or "viewed" or "likes" or "liked" or "comments" or "commented" or "score" => "0",
            _ => "Sg"
        };

    /// CTEs mm (view + metadata columns + site-percentile archive signal Sg) and pm (adds Pm = threshold metric, Ps = sort metric).
    private static string PopularityCtes(PopularityQuery p)
    {
        var pm = p.Metric == "none" ? "0" : $"CASE WHEN HasM THEN {MetricSql(p.Metric, true)} ELSE {MetricSql(p.Metric, false)} END";
        var ps = p.Sort is "newest" or "published" ? "0" : p.SortMetric is "views" or "viewed" ? ViewsSortSql : $"CASE WHEN HasM THEN {MetricSql(p.SortMetric, true)} ELSE {MetricSql(p.SortMetric, false)} END";
        

var hv = p.Sort is "newest" or "published" ? "1" : HasMetricSql(p.SortMetric);
        return $"""
            , base AS (SELECT sg.CaptureId, sg.Host, sg.Raw FROM CaptureSignals sg
                JOIN SearchCaptures sc ON sc.SearchId = sg.SearchId AND sc.CaptureId = sg.CaptureId
                WHERE sg.SearchId = $id AND NOT EXISTS (SELECT 1 FROM PageMetadata m WHERE m.CaptureId = sg.CaptureId)),
            dist AS (SELECT Host, Raw, COUNT(*) AS cnt FROM base GROUP BY Host, Raw),
            cum AS (SELECT Host, Raw,
                SUM(cnt) OVER (PARTITION BY Host ORDER BY Raw) - cnt AS lo,
                SUM(cnt) OVER (PARTITION BY Host ORDER BY Raw) AS hi,
                SUM(cnt) OVER (PARTITION BY Host) AS n FROM dist),
            mm AS (SELECT v.Id, v.CaptureTs, v.Original, v.MimeType, v.StatusCode, v.GroupKey, v.Rd, v.Html,
                m.Views AS mV, m.Likes AS mL, m.Shares AS mSh, m.Comments AS mC, m.Score AS mS, m.EngagementScore AS mE,
                m.Source AS mSource, m.Author AS mAuthor, m.PublishedAtUtc AS mPub, m.Tags AS mTags, m.Confidence AS mConf,
                (m.CaptureId IS NOT NULL) AS HasM,
                CASE WHEN m.CaptureId IS NULL AND cum.n IS NOT NULL
                     THEN 1 + CAST(99.0 * ((cum.lo + cum.hi) / 2.0) / MAX(cum.n, 1) AS INTEGER) ELSE 0 END AS Sg
                FROM v
                LEFT JOIN PageMetadata m ON m.CaptureId = v.Id
                LEFT JOIN CaptureSignals sg ON sg.SearchId = $id AND sg.CaptureId = v.Id
                LEFT JOIN cum ON cum.Host = sg.Host AND cum.Raw = sg.Raw),
            pm0 AS (SELECT mm.*, {pm} AS Pm, {ps} AS Ps, ({ps}) * CASE WHEN HasM THEN 0.5 + 0.5 * COALESCE(mConf, 0) ELSE 1.0 END AS Wk,
                CASE WHEN HasM = 0 THEN 0 WHEN mConf >= 0.5 THEN 2 WHEN mConf >= 0.15 THEN 1 ELSE 0 END AS Tr,
                {hv} AS Hv FROM mm),
            pm AS (SELECT pm0.*, CUME_DIST() OVER (PARTITION BY Hv, Tr ORDER BY Ps) * CASE WHEN HasM THEN 0.5 + 0.5 * COALESCE(mConf, 0) ELSE 1.0 END AS Pk FROM pm0)
            """;
    }

    private FilteredPage? ReadFilteredPage(string prefix, string filter, CaptureFilter f, int page, int pageSize, string? contentTerm,
        CancellationToken ct, PopularityQuery? pop = null)
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

        var (where, args) = FilterSql(id, f, contentTerm: contentTerm);
        var cte = ViewCte(f, where);
        var from = "v";
        var order = "CaptureTs DESC, Id DESC";
        var kept = "";
        if (pop is not null)
        {
            cte += PopularityCtes(pop);
            from = "pm";
            if (pop.Sort == "published") order = "CASE WHEN mPub IS NULL THEN 1 ELSE 0 END, mPub DESC, CaptureTs DESC, Id DESC";
        else if (pop.Sort != "newest") order = pop.Least ? "Hv DESC, Tr DESC, Pk ASC, Wk ASC, CaptureTs DESC, Id DESC" : "Hv DESC, Tr DESC, Pk DESC, Wk DESC, CaptureTs DESC, Id DESC";   // measured > estimated > archive signal
            if (pop.Metric != "none" && (pop.Threshold > 0 || pop.MaxThreshold > 0))
            {
                kept = $" WHERE Pm >= $thr AND Tr >= {pop.MinTier}" + (pop.MaxThreshold > 0 ? " AND Pm <= $maxthr" : "");
                args.Add(("$thr", (long)pop.Threshold));
                if (pop.MaxThreshold > 0) args.Add(("$maxthr", (long)pop.MaxThreshold));
            }
        }

        int everything, total;
        using (var count = Cmd(conn, null, cte + $" SELECT COUNT(*) FROM {from}"))
        {
            Bind(count, args);
            everything = Convert.ToInt32(count.ExecuteScalar());
        }
        if (kept.Length == 0) total = everything;
        else
            using (var count = Cmd(conn, null, cte + $" SELECT COUNT(*) FROM {from}{kept}"))
            {
                Bind(count, args);
                total = Convert.ToInt32(count.ExecuteScalar());
            }
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);

        var rows = new List<StoredCapture>(pageSize);
        var groups = new List<string>();
        Dictionary<string, PageMetadata>? popularity = pop is null ? null : new(StringComparer.Ordinal);
        ct.ThrowIfCancellationRequested();
        var extra = pop is null ? "" : ", mV, mL, mSh, mC, mS, mE, mSource, mAuthor, mPub, mTags, mConf, HasM, Sg";
        using (var cmd = Cmd(conn, null, cte + $" SELECT Id, CaptureTs, Original, MimeType, StatusCode, Rd, GroupKey{extra} FROM {from}{kept} " +
                                         $"ORDER BY {order} LIMIT $limit OFFSET $offset"))
        {
            Bind(cmd, args);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var capture = new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
                rows.Add(new StoredCapture(reader.GetInt64(0), capture, reader.GetInt64(5) != 0));
                groups.Add(reader.IsDBNull(6) ? "" : reader.GetString(6));
                if (popularity is null) continue;
                long? L(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
                if (reader.GetInt64(18) != 0)
                    popularity[capture.Key] = new PageMetadata(L(7), L(8), L(9), L(10), L(11), L(12),
                        reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                        reader.IsDBNull(15) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(15)),
                        reader.IsDBNull(16) ? null : reader.GetString(16), reader.IsDBNull(17) ? 0 : reader.GetDouble(17));
                else if (reader.GetInt64(19) > 0)
                    popularity[capture.Key] = new PageMetadata(EngagementScore: reader.GetInt64(19), Confidence: 0.1);
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
                    using var preset = Cmd(conn, null, cte + $" SELECT Pm FROM pm WHERE Tr >= {pop.MinTier} ORDER BY Pm DESC LIMIT 1 OFFSET $o");
                    Bind(preset, args);
                    preset.Parameters.AddWithValue("$o", index);
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
                using var hist = Cmd(conn, null, cte + $" SELECT CASE WHEN $max <= 0 THEN 0 ELSE MIN({HistogramBuckets - 1}, Pm * {HistogramBuckets} / ($max + 1)) END AS b, COUNT(*) FROM pm WHERE Tr >= {pop.MinTier} GROUP BY b");
                Bind(hist, args);
                hist.Parameters.AddWithValue("$max", max);
                using var reader = hist.ExecuteReader();
                while (reader.Read()) bins[reader.GetInt32(0)] += reader.GetInt32(1);
            }
            histogram = [.. bins];
        }

        int unsavedInView;
        using (var unsaved = Cmd(conn, null, cte + $" SELECT COUNT(*) FROM {from} WHERE Html = 1 AND Rd = 0"))
        {
            Bind(unsaved, args);
            unsavedInView = Convert.ToInt32(unsaved.ExecuteScalar());
        }
        int unsavedTotal, htmlTotal;
        using (var all = Cmd(conn, null, "SELECT COUNT(*) FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId WHERE sc.SearchId = $id AND lower(c.MimeType) LIKE '%html%'"))
        {
            all.Parameters.AddWithValue("$id", id);
            htmlTotal = Convert.ToInt32(all.ExecuteScalar());
        }
        using (var unsaved = Cmd(conn, null, """
                 SELECT COUNT(*) FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId
                 WHERE sc.SearchId = $id AND lower(c.MimeType) LIKE '%html%'
                   AND NOT EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id)
                 """))
        {
            unsaved.Parameters.AddWithValue("$id", id);
            unsavedTotal = Convert.ToInt32(unsaved.ExecuteScalar());
        }

        Dictionary<string, int>? groupSizes = null;
        if (f.Unique && groups.Count > 0)
            groupSizes = ReadGroupSizes(conn, id, f, groups.Distinct(StringComparer.Ordinal).ToList());

        var cachedAt = DateTimeOffset.FromUnixTimeMilliseconds(cachedAtMs);
        return new FilteredPage(id, cachedAt, page, total, rows, ReadFacets(conn, id, cachedAtMs, ct), groupSizes, htmlTotal,
            unsavedTotal, unsavedInView, popularity, everything - total, presets, histogram, pop is null ? 0 : unsavedInView, coverageMeasured, coverageTotal, metricCoverage);
    }
}
