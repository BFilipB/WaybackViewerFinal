using WaybackViewer.Models;

namespace WaybackViewer.Models;

/// One row of the Operations page: a searched URL with its CaptureLoad and Preload progress.
public sealed record OperationRow(
    string Prefix,
    string Filter,
    string State,
    BackgroundOperation? CaptureLoad,
    BackgroundOperation? Preload,
    int Saved,
    int? Skipped,
    int Failed,
    int Pending,
    double PerSecond,
    double? EtaSeconds,
    string? LastError)
{
    public bool Active => (CaptureLoad?.IsActive ?? false) || (Preload?.IsActive ?? false);
    public bool Paused => CaptureLoad?.State == OperationState.Paused || Preload?.State == OperationState.Paused;
    public double CapturePercent => Percent(CaptureLoad);
    public double PreloadPercent => Percent(Preload);

    private static double Percent(BackgroundOperation? op) =>
        op is null || op.Total <= 0 ? (op is not null && OperationStates.IsTerminal(op.State) ? 100 : 0)
            : Math.Round(Math.Min(100, op.Completed * 100.0 / op.Total), 1);
}

public sealed record OperationsPageModel(
    IReadOnlyList<OperationRow> Rows,
    int Total,
    int Page,
    int PageSize,
    string? StateFilter,
    string? TypeFilter,
	string? Search = null,
	string Sort = "updated",
	bool Descending = true,
    int AllTotal = 0)
{
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
    /// True when a search or filter hides some URLs (bulk "all" actions still act on every URL).
    public bool Narrowed => Search is not null || StateFilter is not null || TypeFilter is not null;
}

/// Selected rows for a bulk action. All = every searched URL (ignores Keys).
public sealed record OperationSelection(IReadOnlyList<OperationKeyInput>? Keys, bool All = false);

public sealed record OperationKeyInput(string? Prefix, string? Filter);
