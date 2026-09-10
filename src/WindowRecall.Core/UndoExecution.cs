using System.Collections.Immutable;

namespace WindowRecall.Core;

/// <summary>Executes the stateless mechanics of a one-step undo from a persisted receipt.
/// Ownership of which receipt is currently available is owned by the caller (the in-process
/// <see cref="RestoreCoordinator"/> tracks it in memory; a headless CLI persists one receipt file
/// and consumes it on use). This class never mutates ownership state itself.</summary>
public static class UndoExecution
{
    /// <summary>Reconstructs best-effort move-back operations for every outcome that may have mutated
    /// a window, using only the pre-apply snapshot retained by the receipt.</summary>
    public static async Task<ImmutableArray<WindowOutcome>> RunAsync(
        IWindowSystem windowSystem,
        UndoReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(windowSystem);
        ArgumentNullException.ThrowIfNull(receipt);

        Dictionary<string, WindowSnapshot> beforeById = receipt.BeforeRestore.Windows
            .ToDictionary(window => window.WindowId, StringComparer.Ordinal);
        ImmutableArray<WindowOutcome>.Builder outcomes = ImmutableArray.CreateBuilder<WindowOutcome>();
        foreach (WindowOutcome applied in receipt.Outcomes.Where(WasPotentiallyMutated))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new(applied.SavedWindowId, applied.CurrentWindowId, WindowOutcomeCode.Cancelled,
                    "Undo was cancelled before this item began."));
                continue;
            }
            if (applied.CurrentWindowId is null || !beforeById.TryGetValue(applied.CurrentWindowId, out WindowSnapshot? before))
            {
                outcomes.Add(new(applied.SavedWindowId, applied.CurrentWindowId, WindowOutcomeCode.NotFound,
                    "The pre-apply snapshot does not contain this current window."));
                continue;
            }

            RestorePlanItem item = new(applied.SavedWindowId, before.WindowId, RestoreAction.MoveResize,
                before.NormalBounds, before.State, "Best-effort undo to the immediately pre-apply snapshot.", true);
            RestorePlan undoPlan = new(Guid.NewGuid(), [item], receipt.BeforeRestore);
            try
            {
                ImmutableArray<WindowOutcome> result = await windowSystem
                    .ApplyAsync(undoPlan, [item.SavedWindowId], cancellationToken).ConfigureAwait(false);
                outcomes.Add(result.Length == 1 ? result[0] : new(item.SavedWindowId, item.CurrentWindowId,
                    WindowOutcomeCode.Failed, "Platform adapter did not return exactly one undo outcome."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Cancelled,
                    "Undo was cancelled while this item was being coordinated."));
            }
            catch (Exception exception)
            {
                outcomes.Add(new(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Failed,
                    $"Platform adapter failed during undo: {exception.Message}"));
            }
        }
        return outcomes.ToImmutable();
    }

    private static bool WasPotentiallyMutated(WindowOutcome outcome) =>
        outcome.Code is WindowOutcomeCode.Succeeded or WindowOutcomeCode.Failed or WindowOutcomeCode.PermissionDenied;
}
