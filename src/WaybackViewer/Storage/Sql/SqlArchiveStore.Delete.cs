using WaybackViewer.Models;

namespace WaybackViewer.Storage.Sql;

public sealed partial class SqlArchiveStore
{
    public Task<DeletePreview> GetDeletePreviewAsync(string prefix, string filter, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var conn = Open();
            return DeleteQueries.Preview(conn, prefix, filter, htmlFiles.SizeOf);
        }, ct);

    public Task<int> DeletePagesBatchAsync(string prefix, string filter, int batchSize, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            using var conn = Open();
            var removed = DeleteQueries.DeletePagesBatch(conn, prefix, filter, batchSize);
            foreach (var capture in removed) htmlFiles.Delete(capture);   // after the commit: a crash leaves files, never rows without files
            if (removed.Count > 0) ResetCaches();
            return removed.Count;
        }, ct);

    public Task<int> DeleteCapturesBatchAsync(string prefix, string filter, int batchSize, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            using var conn = Open();
            var (processed, removed) = DeleteQueries.DeleteCapturesBatch(conn, prefix, filter, batchSize);
            foreach (var capture in removed) htmlFiles.Delete(capture);
            if (processed > 0) ResetCaches();
            return processed;
        }, ct);

    public Task DeleteSearchAsync(string prefix, string filter, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            using var conn = Open();
            DeleteQueries.DeleteSearch(conn, prefix, filter);
            ResetCaches();
            return 0;
        }, ct);

    public Task<int> DeleteOperationsAsync(string prefix, string filter, IReadOnlyCollection<OperationKind> kinds,
        CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            using var conn = Open();
            return DeleteQueries.DeleteOperations(conn, prefix, filter, kinds);
        }, ct);

    /// The cached capture lists are rebuilt from the database on next use.
    private void ResetCaches()
    {
        lock (cacheLock)
        {
            searchCache.Clear();
            savedVersion++;
        }
    }
}
