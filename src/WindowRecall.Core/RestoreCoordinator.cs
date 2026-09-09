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
        return await UndoExecution.RunAsync(windowSystem, receipt, cancellationToken).ConfigureAwait(false);
    }
}
