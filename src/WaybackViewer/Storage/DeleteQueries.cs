using System.Data.Common;
using WaybackViewer.Models;

namespace WaybackViewer.Storage;

/// Provider-neutral SQL for deleting a searched URL's data (shared by the SQLite and the dialect-based store).
/// A capture is "exclusive" when no other searched URL lists it; only exclusive captures lose their rows and files.
/// All values are parameters (@name).
internal static class DeleteQueries
{
    private const string MemberOf = """
        FROM SearchCaptures sc
        JOIN Searches s ON s.Id = sc.SearchId
        JOIN Captures c ON c.Id = sc.CaptureId
        WHERE s.UrlPrefix = @p AND s.Filter = @f
        """;

    private const string OnlyHere =
        "NOT EXISTS (SELECT 1 FROM SearchCaptures o WHERE o.CaptureId = sc.CaptureId AND o.SearchId <> sc.SearchId)";

    private static readonly string[] CaptureTables =
        ["Pages", "PageText", "PageMetadata", "CaptureSearch", "PageHtml", "CaptureOutcomes"];

    public static DeletePreview Preview(DbConnection conn, string prefix, string filter, Func<Capture, long> sizeOf)
    {
        int Count(string extra)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) {MemberOf} {extra}";
            Add(cmd, "@p", prefix);
            Add(cmd, "@f", filter);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        var captures = Count("");
        var shared = Count($"AND NOT {OnlyHere}");
        var pages = Count($"AND {OnlyHere} AND EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id)");

        // Sizes come from the files; stream the rows so memory stays bounded.
        long bytes = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT c.CaptureTs, c.Original, c.MimeType, c.StatusCode {MemberOf} AND {OnlyHere} " +
                              "AND EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id)";
            Add(cmd, "@p", prefix);
            Add(cmd, "@f", filter);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) bytes += sizeOf(ReadCapture(reader, 0));
        }
        return new DeletePreview(captures, pages, shared, bytes);
    }

    /// Deletes up to batchSize exclusive pages/outcomes in one transaction and returns their captures
    /// (the caller removes the HTML files after the commit).
    public static IReadOnlyList<Capture> DeletePagesBatch(DbConnection conn, string prefix, string filter, int batchSize)
    {
        var rows = new List<(long Id, Capture Capture)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT c.CaptureTs, c.Original, c.MimeType, c.StatusCode, c.Id {MemberOf} AND {OnlyHere}
                AND (EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = c.Id)
                     OR EXISTS (SELECT 1 FROM CaptureOutcomes x WHERE x.CaptureId = c.Id))
                LIMIT @n
                """;
            Add(cmd, "@p", prefix);
            Add(cmd, "@f", filter);
            Add(cmd, "@n", Math.Max(1, batchSize));
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add((Convert.ToInt64(reader.GetValue(4)), ReadCapture(reader, 0)));
        }
        if (rows.Count == 0) return [];

        using var tx = conn.BeginTransaction();
        foreach (var (id, _) in rows) DeleteCaptureRows(conn, tx, id, includeCapture: false);
        tx.Commit();
        return rows.Select(r => r.Capture).ToList();
    }

    /// Removes up to batchSize captures from the searched URL. Exclusive ones are deleted completely; shared ones
    /// only lose their membership. Returns how many were processed and the exclusive captures (for their files).
    public static (int Processed, IReadOnlyList<Capture> Removed) DeleteCapturesBatch(DbConnection conn, string prefix,
        string filter, int batchSize)
    {
        var rows = new List<(long SearchId, long Id, Capture Capture, bool Exclusive)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT c.CaptureTs, c.Original, c.MimeType, c.StatusCode, c.Id, s.Id,
                       CASE WHEN {OnlyHere} THEN 1 ELSE 0 END {MemberOf} LIMIT @n
                """;
            Add(cmd, "@p", prefix);
            Add(cmd, "@f", filter);
            Add(cmd, "@n", Math.Max(1, batchSize));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add((Convert.ToInt64(reader.GetValue(5)), Convert.ToInt64(reader.GetValue(4)), ReadCapture(reader, 0),
                    Convert.ToInt32(reader.GetValue(6)) == 1));
        }
        if (rows.Count == 0) return (0, []);

        var removed = new List<Capture>();
        using var tx = conn.BeginTransaction();
        foreach (var (searchId, id, capture, exclusive) in rows)
        {
            using (var member = conn.CreateCommand())
            {
                member.Transaction = tx;
                member.CommandText = "DELETE FROM SearchCaptures WHERE SearchId = @s AND CaptureId = @id";
                Add(member, "@s", searchId);
                Add(member, "@id", id);
                member.ExecuteNonQuery();
            }
            if (!exclusive) continue;
            DeleteCaptureRows(conn, tx, id, includeCapture: true);
            removed.Add(capture);
        }
        tx.Commit();
        return (rows.Count, removed);
    }

    public static void DeleteSearch(DbConnection conn, string prefix, string filter)
    {
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "DELETE FROM SearchCaptures WHERE SearchId IN (SELECT Id FROM Searches WHERE UrlPrefix = @p AND Filter = @f)", prefix, filter);
        Exec(conn, tx, "DELETE FROM SearchSummary WHERE SearchId IN (SELECT Id FROM Searches WHERE UrlPrefix = @p AND Filter = @f)", prefix, filter);
        Exec(conn, tx, "DELETE FROM Searches WHERE UrlPrefix = @p AND Filter = @f", prefix, filter);
        tx.Commit();
    }

    public static int DeleteOperations(DbConnection conn, string prefix, string filter, IReadOnlyCollection<OperationKind> kinds)
    {
        if (kinds.Count == 0) return 0;
        using var cmd = conn.CreateCommand();
        var names = new List<string>();
        var i = 0;
        foreach (var kind in kinds)
        {
            names.Add($"@k{i}");
            Add(cmd, $"@k{i}", (int)kind);
            i++;
        }
        cmd.CommandText = $"DELETE FROM Operations WHERE UrlPrefix = @p AND Filter = @f AND Kind IN ({string.Join(", ", names)})";
        Add(cmd, "@p", prefix);
        Add(cmd, "@f", filter);
        return cmd.ExecuteNonQuery();
    }

    private static void DeleteCaptureRows(DbConnection conn, DbTransaction tx, long id, bool includeCapture)
    {
        foreach (var table in CaptureTables)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE CaptureId = @id";
            Add(cmd, "@id", id);
            cmd.ExecuteNonQuery();
        }
        if (!includeCapture) return;
        using var capture = conn.CreateCommand();
        capture.Transaction = tx;
        capture.CommandText = "DELETE FROM Captures WHERE Id = @id";
        Add(capture, "@id", id);
        capture.ExecuteNonQuery();
    }

    private static void Exec(DbConnection conn, DbTransaction tx, string sql, string prefix, string filter)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Add(cmd, "@p", prefix);
        Add(cmd, "@f", filter);
        cmd.ExecuteNonQuery();
    }

    private static Capture ReadCapture(DbDataReader reader, int first) =>
        new(reader.GetString(first), reader.GetString(first + 1), reader.GetString(first + 2), reader.GetString(first + 3));

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
