namespace WaybackViewer.Models;

/// The operation state machine. Every state change goes through <see cref="CanTransition"/>.
///   Queued      -> Running, Paused, Failed, Completed, CompletedWithFailures, Deleting
///   Running     -> Paused, Interrupted, Completed, CompletedWithFailures, Failed, Deleting
///   Paused      -> Running, Queued, Completed*, Failed, Deleting  (pause survives restarts; * last in-flight item finished)
///   Interrupted -> Running, Queued, Paused, Completed*, Failed, Deleting (resumed on startup)
///   Completed / CompletedWithFailures / Failed -> Queued, Running (a new run), Deleting
///   Deleting    -> (nothing)
/// Legacy Stopping/Stopped rows from older databases behave like Paused.
public static class OperationStates
{
    /// Unfinished: holds the (kind, url) slot, so a second request reuses it instead of starting a duplicate.
    public static bool IsActive(OperationState state) => state is OperationState.Queued or OperationState.Running
        or OperationState.Paused or OperationState.Interrupted or OperationState.Stopping or OperationState.Stopped
        or OperationState.Deleting;

    public static bool IsTerminal(OperationState state) => state is OperationState.Completed
        or OperationState.CompletedWithFailures or OperationState.Failed;

    /// Maps legacy values to the states the app still uses.
    public static OperationState Normalize(OperationState state) =>
        state is OperationState.Stopping or OperationState.Stopped ? OperationState.Paused : state;

    /// The numeric values of the active states, for SQL "State IN (...)".
    public const string ActiveSqlList = "0, 1, 2, 3, 4, 5, 9";

    public static bool CanTransition(OperationState from, OperationState to)
    {
        from = Normalize(from);
        to = Normalize(to);
        if (from == to) return from != OperationState.Deleting;   // progress updates keep the state
        return from switch
        {
            OperationState.Queued => to is OperationState.Running or OperationState.Paused or OperationState.Interrupted or OperationState.Failed
                or OperationState.Completed or OperationState.CompletedWithFailures or OperationState.Deleting,
            OperationState.Running => to is OperationState.Paused or OperationState.Interrupted or OperationState.Completed
                or OperationState.CompletedWithFailures or OperationState.Failed or OperationState.Deleting,
            OperationState.Paused => to is OperationState.Running or OperationState.Queued or OperationState.Completed
                or OperationState.CompletedWithFailures or OperationState.Failed or OperationState.Deleting,
            OperationState.Interrupted => to is OperationState.Running or OperationState.Queued or OperationState.Paused
                or OperationState.Completed or OperationState.CompletedWithFailures or OperationState.Failed or OperationState.Deleting,
            OperationState.Completed or OperationState.CompletedWithFailures or OperationState.Failed =>
                to is OperationState.Queued or OperationState.Running or OperationState.Deleting,
            _ => false
        };
    }
}
