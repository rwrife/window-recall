using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

public sealed class RestoreCoordinatorTests
{
    [Fact]
    public async Task CapturesImmediatelyBeforeApplyAndOnlyExecutesIncludedItems()
    {
        FakeWindowSystem system = new();
        RestorePlan plan = Plan(
            Item("included", "current-1", true),
            Item("excluded", "current-2", false));

        UndoReceipt receipt = await new RestoreCoordinator(system).ApplyAsync(plan);

        Assert.Equal(["capture", "apply:included"], system.Calls);
        Assert.Same(system.Captured, receipt.BeforeRestore);
        Assert.Collection(receipt.Outcomes,
            outcome => Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code),
            outcome => Assert.Equal(WindowOutcomeCode.Skipped, outcome.Code));
    }

    [Fact]
    public async Task CancellationBetweenItemsPreservesPartialOutcomes()
    {
        using CancellationTokenSource cancellation = new();
        FakeWindowSystem system = new()
        {
            AfterApply = id => { if (id == "one") cancellation.Cancel(); },
        };
        RestorePlan plan = Plan(Item("one", "current-1", true), Item("two", "current-2", true));

        UndoReceipt receipt = await new RestoreCoordinator(system).ApplyAsync(plan, cancellation.Token);

        Assert.Equal(["capture", "apply:one"], system.Calls);
        Assert.Equal(WindowOutcomeCode.Succeeded, receipt.Outcomes[0].Code);
        Assert.Equal(WindowOutcomeCode.Cancelled, receipt.Outcomes[1].Code);
    }

    [Fact]
    public async Task AdapterFailuresDoNotPreventLaterItemsAndRemainInReceipt()
    {
        FakeWindowSystem system = new() { Fail = "one" };

        UndoReceipt receipt = await new RestoreCoordinator(system).ApplyAsync(
            Plan(Item("one", "current-1", true), Item("two", "current-2", true)));

        Assert.Equal([WindowOutcomeCode.Failed, WindowOutcomeCode.Succeeded], receipt.Outcomes.Select(outcome => outcome.Code));
        Assert.Equal(["capture", "apply:one", "apply:two"], system.Calls);
    }

    [Fact]
    public async Task UndoIsBestEffortAndReceiptCanBeConsumedOnlyOnce()
    {
        FakeWindowSystem system = new() { Fail = "two" };
        RestoreCoordinator coordinator = new(system);
        UndoReceipt receipt = await coordinator.ApplyAsync(
            Plan(Item("one", "current-1", true), Item("two", "current-2", true)));
        system.Fail = "one";
        system.Calls.Clear();

        ImmutableArray<WindowOutcome> undo = await coordinator.UndoAsync(receipt);

        Assert.Equal(["apply:one", "apply:two"], system.Calls);
        Assert.Equal([WindowOutcomeCode.Failed, WindowOutcomeCode.Succeeded], undo.Select(outcome => outcome.Code));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.UndoAsync(receipt));
    }

    [Fact]
    public async Task NewApplyInvalidatesPreviousOneStepUndo()
    {
        FakeWindowSystem system = new();
        RestoreCoordinator coordinator = new(system);
        UndoReceipt first = await coordinator.ApplyAsync(Plan(Item("one", "current-1", true)));
        _ = await coordinator.ApplyAsync(Plan(Item("two", "current-2", true)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.UndoAsync(first));
    }

    private static RestorePlan Plan(params RestorePlanItem[] items) =>
        new(Guid.NewGuid(), items.ToImmutableArray(), Desktop());

    private static RestorePlanItem Item(string savedId, string currentId, bool included) =>
        new(savedId, currentId, RestoreAction.MoveResize, new(10, 10, 200, 100), WindowState.Normal, "test", included);

    private static CurrentDesktop Desktop() => new(DateTimeOffset.UtcNow,
        [new("display", null, new(0, 0, 1000, 800), new(0, 0, 1000, 760), 1, DisplayOrientation.Landscape, true)],
        [
            new("current-1", new("app-1"), null, null, new(1, 2, 300, 200), WindowState.Maximized, "display"),
            new("current-2", new("app-2"), null, null, new(3, 4, 400, 300), WindowState.Minimized, "display"),
        ]);

    private sealed class FakeWindowSystem : IWindowSystem
    {
        public List<string> Calls { get; } = [];
        public CurrentDesktop Captured { get; } = Desktop();
        public string? Fail { get; set; }
        public Action<string>? AfterApply { get; init; }

        public Task<CurrentDesktop> CaptureAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("capture");
            return Task.FromResult(Captured);
        }

        public Task<ImmutableArray<WindowOutcome>> ApplyAsync(RestorePlan plan, ImmutableArray<string> approvedSavedWindowIds,
            CancellationToken cancellationToken = default)
        {
            RestorePlanItem item = Assert.Single(plan.Items);
            Calls.Add($"apply:{item.SavedWindowId}");
            WindowOutcomeCode code = item.SavedWindowId == Fail ? WindowOutcomeCode.Failed : WindowOutcomeCode.Succeeded;
            AfterApply?.Invoke(item.SavedWindowId);
            return Task.FromResult<ImmutableArray<WindowOutcome>>([new(item.SavedWindowId, item.CurrentWindowId, code, "fake")]);
        }

        public Task<WindowSystemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowSystemCapabilities(true, true, true, false, null));
    }
}
