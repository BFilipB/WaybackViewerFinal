using System.Data.Common;
using System.Globalization;
using System.Text;
using WaybackViewer.Models;

namespace WaybackViewer.Storage;

/// Provider-neutral SQL for the URL summary table (SearchSummary), the Searched URLs list (keyset paging),
/// autocomplete and the per-URL capture pages. Shared by the SQLite and the dialect-based store. All values are
/// parameters (@name); only fixed fragments are concatenated.
internal static class UrlQueries
{
    public const int MaxLimit = 100;

    private const string SummarySelect = """
        SELECT s.Id, s.UrlPrefix, s.Filter, s.CachedAtUtc, m.CaptureCount, m.FirstTs, m.LastTs,
               (SELECT COUNT(*) FROM SearchCaptures sc JOIN Pages p ON p.CaptureId = sc.CaptureId WHERE sc.SearchId = s.Id)
        FROM Searches s JOIN SearchSummary m ON m.SearchId = s.Id
        """;

    /// Recomputes the summary row of one search (call inside the transaction that changed its capture list).
    public static void RefreshSummary(DbConnection conn, DbTransaction? tx, long searchId)
    {
        Run(conn, tx, "DELETE FROM SearchSummary WHERE SearchId = @id", ("@id", searchId));
        Run(conn, tx, """
            INSERT INTO SearchSummary (SearchId, CaptureCount, FirstTs, LastTs)
            SELECT @id, COUNT(*), MIN(c.CaptureTs), MAX(c.CaptureTs)
            FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId WHERE sc.SearchId = @id
            """, ("@id", searchId));
    }

    /// Creates the summary rows that do not exist yet (existing databases; a no-op once everything is summarised).
    public static int Backfill(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SearchSummary (SearchId, CaptureCount, FirstTs, LastTs)
            SELECT s.Id, COUNT(c.Id), MIN(c.CaptureTs), MAX(c.CaptureTs)
            FROM Searches s
            LEFT JOIN SearchCaptures sc ON sc.SearchId = s.Id
            LEFT JOIN Captures c ON c.Id = sc.CaptureId
            WHERE NOT EXISTS (SELECT 1 FROM SearchSummary m WHERE m.SearchId = s.Id)
            GROUP BY s.Id
            """;
        return cmd.ExecuteNonQuery();
    }

    public static UrlListPage List(DbConnection conn, UrlListQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, MaxLimit);
        var sort = UrlSorts.Normalize(query.Sort);
        var sql = new StringBuilder(SummarySelect).Append(" WHERE 1 = 1");
        using var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            sql.Append(" AND s.UrlPrefix LIKE @q ESCAPE '\\'");
            Add(cmd, "@q", "%" + EscapeLike(query.Search.Trim()) + "%");
        }

        if (query.AfterKey is not null)
        {
            Add(cmd, "@id", query.AfterId);
            if (sort == UrlSorts.Alpha)
            {
                sql.Append(" AND (s.UrlPrefix > @k OR (s.UrlPrefix = @k AND s.Id > @id))");
                Add(cmd, "@k", query.AfterKey);
            }
            else
            {
                if (!long.TryParse(query.AfterKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key))
                    throw new ArgumentException("Invalid cursor.", nameof(query));
                var column = sort == UrlSorts.Recent ? "s.CachedAtUtc" : "m.CaptureCount";
                sql.Append(CultureInfo.InvariantCulture, $" AND ({column} < @k OR ({column} = @k AND s.Id < @id))");
                Add(cmd, "@k", key);
            }
        }

        sql.Append(sort switch
        {
            UrlSorts.Alpha => " ORDER BY s.UrlPrefix ASC, s.Id ASC",
            UrlSorts.Recent => " ORDER BY s.CachedAtUtc DESC, s.Id DESC",
            _ => " ORDER BY m.CaptureCount DESC, s.Id DESC"
        });
        sql.Append(" LIMIT @lim");
        Add(cmd, "@lim", limit + 1);
        cmd.CommandText = sql.ToString();

        var items = new List<UrlSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) items.Add(ReadSummary(reader));
        var more = items.Count > limit;
        if (more) items.RemoveAt(items.Count - 1);
        return new UrlListPage(items, more);
    }

    public static UrlSummary? Get(DbConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SummarySelect + " WHERE s.Id = @id";
        Add(cmd, "@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadSummary(reader) : null;
    }

    public const int MaxSuggestions = 200;

    public static IReadOnlyList<UrlSuggestion> Suggest(DbConnection conn, string prefix, int limit)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.UrlPrefix, MAX(m.CaptureCount)
            FROM Searches s JOIN SearchSummary m ON m.SearchId = s.Id
            WHERE s.UrlPrefix LIKE @q ESCAPE '\'
            GROUP BY s.UrlPrefix
            ORDER BY MAX(m.CaptureCount) DESC, s.UrlPrefix ASC
            LIMIT @lim
            """;
        Add(cmd, "@q", "%" + EscapeLike(prefix.Trim()) + "%");   // anywhere in the URL; empty = all
        Add(cmd, "@lim", Math.Clamp(limit, 1, MaxSuggestions));
        var result = new List<UrlSuggestion>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(new UrlSuggestion(reader.GetString(0), Convert.ToInt32(reader.GetValue(1))));
        return result;
    }

    public static CaptureRowsPage Captures(DbConnection conn, long searchId, UrlCaptureFilter filter, int offset, int limit)
    {
        var where = new StringBuilder(" WHERE sc.SearchId = @id");
        using var count = conn.CreateCommand();
        using var rows = conn.CreateCommand();
        foreach (var cmd in new[] { count, rows })
        {
            Add(cmd, "@id", searchId);
            if (filter.Mime is not null) Add(cmd, "@mime", filter.Mime);
            if (filter.Status is not null) Add(cmd, "@status", filter.Status);
            if (filter.FromTimestamp is not null) Add(cmd, "@from", filter.FromTimestamp);
            if (filter.ToTimestamp is not null) Add(cmd, "@to", filter.ToTimestamp);
        }
        if (filter.Mime is not null) where.Append(" AND c.MimeType = @mime");
        if (filter.Status is not null) where.Append(" AND c.StatusCode = @status");
        if (filter.FromTimestamp is not null) where.Append(" AND c.CaptureTs >= @from");
        if (filter.ToTimestamp is not null) where.Append(" AND c.CaptureTs <= @to");
        const string from = " FROM SearchCaptures sc JOIN Captures c ON c.Id = sc.CaptureId";

        count.CommandText = "SELECT COUNT(*)" + from + where;
        var total = Convert.ToInt32(count.ExecuteScalar());

        rows.CommandText = "SELECT c.Id, c.CaptureTs, c.Original, c.MimeType, c.StatusCode, " +
                           "CASE WHEN EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id) THEN 1 ELSE 0 END" +
                           from + where + " ORDER BY c.CaptureTs DESC, c.Id DESC LIMIT @lim OFFSET @off";
        Add(rows, "@lim", Math.Clamp(limit, 1, MaxLimit));
        Add(rows, "@off", Math.Max(0, offset));
        var items = new List<StoredCapture>();
        using var reader = rows.ExecuteReader();
        while (reader.Read())
            items.Add(new StoredCapture(reader.GetInt64(0),
                new Capture(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)),
                Convert.ToInt32(reader.GetValue(5)) == 1));
        return new CaptureRowsPage(items, total);
    }

    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static UrlSummary ReadSummary(DbDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), Convert.ToInt32(r.GetValue(4)),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
            Convert.ToInt32(r.GetValue(7)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)));

    private static void Run(DbConnection conn, DbTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) Add(cmd, name, value);
        cmd.ExecuteNonQuery();
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
