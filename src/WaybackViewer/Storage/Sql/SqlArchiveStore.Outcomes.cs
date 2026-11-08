using System.Data;
using WaybackViewer.Models;

namespace WaybackViewer.Storage.Sql;

public sealed partial class SqlArchiveStore
{
    public Task<IReadOnlyList<BackgroundOperation>> GetOperationsForSearchAsync(string prefix, string filter,
        CancellationToken ct = default) => Task.Run(() =>
        {
            using var conn = Open();
            return OperationQueries.ForSearch(conn, prefix, filter, ReadOperation);
        }, ct);

    public Task<int> CountOtherActiveSearchesAsync(string prefix, string filter, CancellationToken ct = default) =>
        Task.Run(() => { using var conn = Open(); return OperationQueries.CountOtherActive(conn, prefix, filter); }, ct);

    public Task<OperationSearchPage> ListOperationSearchesAsync(OperationListQuery query, CancellationToken ct = default) =>
        Task.Run(() => { using var conn = Open(); return OperationQueries.ListSearches(conn, query); }, ct);

    public Task<IReadOnlyList<BackgroundOperation>> GetOperationsForSearchesAsync(IReadOnlyCollection<SearchKey> searches,
        CancellationToken ct = default) => Task.Run(() =>
        {
            using var conn = Open();
            return OperationQueries.ForSearches(conn, searches, ReadOperation);
        }, ct);

    public Task SaveOutcomesAsync(IReadOnlyList<CaptureOutcome> outcomes, CancellationToken ct = default) =>
        outcomes.Count == 0 ? Task.CompletedTask : WriteAsync(() =>
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var insertCapture = Cmd(conn, tx, dialect.InsertIgnore("Captures",
                ["CaptureTs", "Original", "MimeType", "StatusCode"], "VALUES (@ts, @o, @m, @s)", ["CaptureTs", "Original"]));
            var cTs = Typed(insertCapture, "@ts", DbType.String);
            var cO = Typed(insertCapture, "@o", DbType.String);
            var cM = Typed(insertCapture, "@m", DbType.String);
            var cS = Typed(insertCapture, "@s", DbType.String);
            using var remove = Cmd(conn, tx,
                "DELETE FROM CaptureOutcomes WHERE CaptureId IN (SELECT Id FROM Captures WHERE CaptureTs = @ts AND Original = @o)");
            var rTs = Typed(remove, "@ts", DbType.String);
            var rO = Typed(remove, "@o", DbType.String);
            using var insert = Cmd(conn, tx,
                "INSERT INTO CaptureOutcomes (CaptureId, Outcome, Reason, UpdatedAtUtc) " +
                "SELECT Id, @outcome, @reason, @at FROM Captures WHERE CaptureTs = @ts AND Original = @o");
            var iTs = Typed(insert, "@ts", DbType.String);
            var iO = Typed(insert, "@o", DbType.String);
            var iOutcome = Typed(insert, "@outcome", DbType.Int32);
            var iReason = Typed(insert, "@reason", DbType.String);
            var iAt = Typed(insert, "@at", DbType.Int64);
            foreach (var outcome in outcomes)
            {
                ct.ThrowIfCancellationRequested();
                cTs.Value = rTs.Value = iTs.Value = outcome.Capture.Timestamp;
                cO.Value = rO.Value = iO.Value = outcome.Capture.Original;
                cM.Value = outcome.Capture.MimeType;
                cS.Value = outcome.Capture.StatusCode;
                insertCapture.ExecuteNonQuery();
                remove.ExecuteNonQuery();
                iOutcome.Value = (int)outcome.Outcome;
                iReason.Value = outcome.Reason;
                iAt.Value = outcome.At.ToUnixTimeMilliseconds();
                insert.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        }, ct);

    public Task<IReadOnlyDictionary<string, CaptureOutcome>> GetOutcomesAsync(string prefix, string filter,
        CancellationToken ct = default) =>
        Task.Run<IReadOnlyDictionary<string, CaptureOutcome>>(() =>
        {
            using var conn = Open();
            using var cmd = Cmd(conn, null, """
                SELECT c.CaptureTs, c.Original, c.MimeType, c.StatusCode, o.Outcome, o.Reason, o.UpdatedAtUtc
                FROM Searches s
                JOIN SearchCaptures sc ON sc.SearchId = s.Id
                JOIN CaptureOutcomes o ON o.CaptureId = sc.CaptureId
                JOIN Captures c ON c.Id = o.CaptureId
                WHERE s.UrlPrefix = @p AND s.Filter = @f
                  AND NOT EXISTS (SELECT 1 FROM Pages p WHERE p.CaptureId = o.CaptureId)
                """);
            Param(cmd, "@p", prefix);
            Param(cmd, "@f", filter);
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
            DELETE FROM CaptureOutcomes WHERE Outcome = @failed AND CaptureId IN (
                SELECT sc.CaptureId FROM Searches s JOIN SearchCaptures sc ON sc.SearchId = s.Id
                WHERE s.UrlPrefix = @p AND s.Filter = @f)
            """);
        Param(cmd, "@failed", (int)PreloadOutcome.Failed);
        Param(cmd, "@p", prefix);
        Param(cmd, "@f", filter);
        return cmd.ExecuteNonQuery();
    }, ct);
}
