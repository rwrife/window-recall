using System.Collections.Immutable;

namespace WindowRecall.Core;

/// <summary>Coordinates selective execution, cancellation boundaries, partial outcomes, and one-step undo.</summary>
public sealed class RestoreCoordinator : IRestoreCoordinator
{
    private readonly IWindowSystem windowSystem;
    private Guid? availableUndoReceiptId;

    public RestoreCoordinator(IWindowSystem windowSystem) =>
        this.windowSystem = windowSystem ?? throw new ArgumentNullException(nameof(windowSystem));

    public async Task<UndoReceipt> ApplyAsync(RestorePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CurrentDesktop before = await windowSystem.CaptureAsync(cancellationToken).ConfigureAwait(false);
        ImmutableArray<WindowOutcome>.Builder outcomes = ImmutableArray.CreateBuilder<WindowOutcome>(plan.Items.Length);
        foreach (RestorePlanItem item in plan.Items)
        {
            if (!item.IsIncluded)
            {
                outcomes.Add(new(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Skipped,
                    "Preview item was not selected."));
                continue;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Cancelled,
                    "Restore was cancelled before this item began."));
                continue;
            }

            RestorePlan single = new(plan.PlanId, [item], before);
            try
            {
                ImmutableArray<WindowOutcome> result = await windowSystem
                    .ApplyAsync(single, [item.SavedWindowId], cancellationToken).ConfigureAwait(false);
                outcomes.Add(result.Length == 1 ? result[0] : new(item.SavedWindowId, item.CurrentWindowId,
                    WindowOutcomeCode.Failed, "Platform adapter did not return exactly one outcome."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Cancelled,
                    "Restore was cancelled while this item was being coordinated."));
            }
            catch (Exception exception)
            {
                outcomes.Add(new(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Failed,
                    $"Platform adapter failed: {exception.Message}"));
            }
        }

        UndoReceipt receipt = new(Guid.NewGuid(), plan.PlanId, DateTimeOffset.UtcNow, before, outcomes.ToImmutable());
        availableUndoReceiptId = receipt.ReceiptId;
        return receipt;
    }

    public async Task<ImmutableArray<WindowOutcome>> UndoAsync(
        UndoReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        if (availableUndoReceiptId != receipt.ReceiptId)
            throw new InvalidOperationException("This undo receipt is no longer the available one-step undo operation.");
        availableUndoReceiptId = null;

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
