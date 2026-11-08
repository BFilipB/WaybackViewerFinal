using WaybackViewer.Models;

namespace WaybackViewer.Storage;

public sealed partial class InMemoryArchiveStore
{
    public Task<DeletePreview> GetDeletePreviewAsync(string prefix, string filter, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!searches.TryGetValue(SearchKey(prefix, filter), out var search)) return Task.FromResult(new DeletePreview(0, 0, 0, 0));
            var others = OtherMembersLocked(SearchKey(prefix, filter));
            var shared = search.CaptureIds.Count(others.Contains);
            var pageIds = search.CaptureIds.Where(id => !others.Contains(id) && pages.ContainsKey(id)).ToList();
            long bytes = pageIds.Sum(id => htmlFiles.SizeOf(captures[id]));
            return Task.FromResult(new DeletePreview(search.CaptureIds.Count, pageIds.Count, shared, bytes));
        }
    }

    public Task<int> DeletePagesBatchAsync(string prefix, string filter, int batchSize, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!searches.TryGetValue(SearchKey(prefix, filter), out var search)) return Task.FromResult(0);
            var others = OtherMembersLocked(SearchKey(prefix, filter));
            var batch = search.CaptureIds.Where(id => !others.Contains(id) && (pages.ContainsKey(id) || outcomes.ContainsKey(id)))
                .Take(Math.Max(1, batchSize)).ToList();
            foreach (var id in batch) RemoveCaptureDataLocked(id);
            return Task.FromResult(batch.Count);
        }
    }

    public Task<int> DeleteCapturesBatchAsync(string prefix, string filter, int batchSize, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!searches.TryGetValue(SearchKey(prefix, filter), out var search)) return Task.FromResult(0);
            var others = OtherMembersLocked(SearchKey(prefix, filter));
            var batch = search.CaptureIds.Take(Math.Max(1, batchSize)).ToList();
            foreach (var id in batch)
            {
                search.CaptureIds.Remove(id);
                if (others.Contains(id)) continue;
                RemoveCaptureDataLocked(id);
                captureIds.Remove(Key(captures[id].Timestamp, captures[id].Original));
                captures.Remove(id);
            }
            return Task.FromResult(batch.Count);
        }
    }

    public Task DeleteSearchAsync(string prefix, string filter, CancellationToken ct = default)
    {
        lock (gate) searches.Remove(SearchKey(prefix, filter));
        return Task.CompletedTask;
    }

    public Task<int> DeleteOperationsAsync(string prefix, string filter, IReadOnlyCollection<OperationKind> kinds,
        CancellationToken ct = default)
    {
        lock (gate)
        {
            var removed = 0;
            foreach (var kind in kinds)
                if (operations.Remove(OperationKey(prefix, filter, kind))) removed++;
            return Task.FromResult(removed);
        }
    }

    private HashSet<long> OtherMembersLocked(string exceptKey)
    {
        var set = new HashSet<long>();
        foreach (var (key, search) in searches)
            if (key != exceptKey) set.UnionWith(search.CaptureIds);
        return set;
    }

    private void RemoveCaptureDataLocked(long id)
    {
        if (pages.Remove(id, out var page)) htmlFiles.Delete(page.Capture);
        outcomes.Remove(id);
        captureSearchText.Remove(id);
    }
}
