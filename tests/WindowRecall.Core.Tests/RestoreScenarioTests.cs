using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

/// <summary>End-to-end deterministic restore scenarios across the Core stack (matcher, topology
/// mapper, planner, coordinator, and undo) with the in-memory window system. These exercise the
/// issue #7 scenario list that is automatable without a live desktop: laptop-only, external-only,
/// extended desktop, display removal, negative coordinates, scale mismatch, rotation, minimized/
/// maximized state, multi-window same-app ambiguity, rejected move, and partial failure. They are
/// deterministic Core evidence only and are explicitly NOT a substitute for live Windows/macOS
/// desktop integration runs.</summary>
public sealed class RestoreScenarioTests
{
    [Fact]
    public async Task UndockFromExtendedToLaptopOnly_ClampsEveryWindowIntoLaptopWorkArea()
    {
        DisplaySnapshot laptop = Display("laptop", 0, 0, 1920, 1040, scale: 1, primary: true);
        DisplaySnapshot external = Display("external", 1920, 0, 2560, 1400, scale: 1, primary: false);
        LayoutProfile docked = Profile(
            [laptop, external],
            Window("ide", "com.example.ide", new DesktopRect(2000, 100, 1400, 900), WindowState.Normal, "external"),
            Window("mail", "com.example.mail", new DesktopRect(50, 60, 900, 600), WindowState.Normal, "laptop"),
            Window("terminal", "com.example.term", new DesktopRect(3000, 1100, 1200, 260), WindowState.Normal, "external"));

        // Undocked: the external display is gone entirely (display removal scenario).
        InMemoryWindowSystem current = new(Desktop([laptop],
            Window("ide-here", "com.example.ide", new DesktopRect(30, 30, 800, 500), WindowState.Normal, "laptop"),
            Window("mail-here", "com.example.mail", new DesktopRect(60, 60, 700, 480), WindowState.Normal, "laptop"),
            Window("term-here", "com.example.term", new DesktopRect(90, 90, 690, 460), WindowState.Normal, "laptop")));
        RestorePlan plan = await new RestorePlanner().CreatePlanAsync(docked, await current.CaptureAsync());
        Assert.All(plan.Items, item =>
        {
            Assert.Equal(RestoreAction.MoveResize, item.Action);
            Assert.True(item.IsIncluded);
        });

        UndoReceipt receipt = await new RestoreCoordinator(current).ApplyAsync(plan);
        Assert.All(receipt.Outcomes, outcome => Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code));

        CurrentDesktop after = await current.CaptureAsync();
        DesktopRect work = laptop.WorkArea;
        Assert.All(after.Windows, window =>
        {
            // No window may sit entirely outside the only remaining work area.
            Assert.True(window.NormalBounds.X < work.X + work.Width - RestoreGeometry.MinimumVisibleWidth,
                $"{window.WindowId} escaped right of the laptop work area");
            Assert.True(window.NormalBounds.X + window.NormalBounds.Width > work.X + RestoreGeometry.MinimumVisibleWidth,
                $"{window.WindowId} escaped left of the laptop work area");
            Assert.True(window.NormalBounds.Y + window.NormalBounds.Height > work.Y);
            Assert.True(window.NormalBounds.Y < work.Y + work.Height - RestoreGeometry.MinimumVisibleHeight);
        });
    }

    [Fact]
    public async Task LaptopOnlyToExternalOnly_RestoresOntoExternalIncludingNegativeOrigins()
    {
        DisplaySnapshot laptop = Display("laptop", 0, 0, 1920, 1040, 1, primary: true);
        LayoutProfile small = Profile(
            [laptop],
            Window("editor", "com.example.editor", new DesktopRect(100, 80, 1200, 700), WindowState.Normal, "laptop"));

        // Docked with only a laptop-left-of-external topology (negative coordinates), laptop closed.
        DisplaySnapshot external = Display("external", -2560, 0, 2560, 1400, scale: 2, primary: true);
        InMemoryWindowSystem current = new(Desktop([external],
            Window("editor-here", "com.example.editor", new DesktopRect(-2000, 200, 900, 600), WindowState.Normal, "external")));
        RestorePlan plan = await new RestorePlanner().CreatePlanAsync(small, await current.CaptureAsync());

        UndoReceipt receipt = await new RestoreCoordinator(current).ApplyAsync(plan);
        Assert.Equal(WindowOutcomeCode.Succeeded, Assert.Single(receipt.Outcomes).Code);
        WindowSnapshot moved = Assert.Single((await current.CaptureAsync()).Windows);
        DesktopRect work = external.WorkArea;
        Assert.True(moved.NormalBounds.X >= work.X);
        Assert.True(moved.NormalBounds.Y >= work.Y);
        Assert.True(moved.NormalBounds.X + moved.NormalBounds.Width <= work.X + work.Width);
    }

    [Fact]
    public async Task ScaleMismatchAndRotatedTarget_MapNormalizedGeometryOntoPortraitHighDpi()
    {
        DisplaySnapshot saved = Display("saved", 0, 0, 1920, 1040, scale: 1, primary: true);
        LayoutProfile profile = Profile([saved],
            Window("doc", "com.example.docs", new DesktopRect(960, 520, 960, 520), WindowState.Normal, "saved"));

        DisplaySnapshot rotated = Display("rotated", -100, -1200, 900, 1500, scale: 2, primary: true, DisplayOrientation.Portrait);
        InMemoryWindowSystem current = new(Desktop([rotated],
            Window("doc-here", "com.example.docs", new DesktopRect(-50, -1100, 300, 200), WindowState.Normal, "rotated")));
        RestorePlan plan = await new RestorePlanner().CreatePlanAsync(profile, await current.CaptureAsync());
        RestorePlanItem item = Assert.Single(plan.Items);
        Assert.NotNull(item.TargetBounds);

        UndoReceipt receipt = await new RestoreCoordinator(current).ApplyAsync(plan);
        Assert.Equal(WindowOutcomeCode.Succeeded, Assert.Single(receipt.Outcomes).Code);
        DesktopRect target = item.TargetBounds!;
        DesktopRect work = rotated.WorkArea;
        Assert.True(target.X >= work.X && target.Y >= work.Y);
        Assert.True(target.X + target.Width <= work.X + work.Width);
        Assert.True(target.Y + target.Height <= work.Y + work.Height);
    }

    [Fact]
    public async Task MinimizedAndMaximizedStatesSurvivePreviewApplyAndUndo()
    {
        DisplaySnapshot display = Display("display", 0, 0, 1920, 1040, 1, primary: true);
        LayoutProfile profile = Profile([display],
            Window("min", "com.example.min", new DesktopRect(40, 40, 800, 500), WindowState.Minimized, "display"),
            Window("max", "com.example.max", new DesktopRect(80, 80, 900, 600), WindowState.Maximized, "display"));
        CurrentDesktop before = Desktop([display],
            Window("min-now", "com.example.min", new DesktopRect(300, 300, 500, 400), WindowState.Normal, "display"),
            Window("max-now", "com.example.max", new DesktopRect(400, 400, 600, 450), WindowState.Normal, "display"));

        RestorePlan plan = await new RestorePlanner().CreatePlanAsync(profile, before);
        Dictionary<string, WindowState> previewStates = plan.Items.ToDictionary(item => item.SavedWindowId, item => item.TargetState!.Value);
        Assert.Equal(
            new Dictionary<string, WindowState> { ["min"] = WindowState.Minimized, ["max"] = WindowState.Maximized },
            previewStates);

        InMemoryWindowSystem system = new(before);
        RestoreCoordinator coordinator = new(system);
        UndoReceipt receipt = await coordinator.ApplyAsync(plan);
        Assert.All(receipt.Outcomes, outcome => Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code));
        Assert.All(
            (await system.CaptureAsync()).Windows,
            window => Assert.Equal(
                window.Application.ApplicationId == "com.example.min" ? WindowState.Minimized : WindowState.Maximized,
                window.State));

        ImmutableArray<WindowOutcome> undone = await coordinator.UndoAsync(receipt);
        Assert.All(undone, outcome => Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code));
        Assert.All((await system.CaptureAsync()).Windows, window => Assert.Equal(WindowState.Normal, window.State));
    }

    [Fact]
    public async Task SameAppMultipleWindowsStayAmbiguousThroughTheWholePipeline()
    {
        DisplaySnapshot display = Display("display", 0, 0, 1920, 1040, 1, primary: true);
        LayoutProfile profile = Profile([display],
            Window("one", "com.example.editor", new DesktopRect(0, 0, 800, 500), WindowState.Normal, "display"));
        CurrentDesktop current = Desktop([display],
            Window("a", "com.example.editor", new DesktopRect(10, 10, 700, 400), WindowState.Normal, "display"),
            Window("b", "com.example.editor", new DesktopRect(20, 20, 700, 400), WindowState.Normal, "display"));

        RestorePlan plan = await new RestorePlanner().CreatePlanAsync(profile, current);
        RestorePlanItem item = Assert.Single(plan.Items);
        Assert.Equal(RestoreAction.Ambiguous, item.Action);
        Assert.False(item.CanAutoApply);

        // A selection attempt cannot force an ambiguous item to auto-apply.
        InMemoryWindowSystem system = new(current);
        RestoreCoordinator coordinator = new(system);
        UndoReceipt receipt = await coordinator.ApplyAsync(plan.WithSelection([item.SavedWindowId]));
        Assert.Equal(WindowOutcomeCode.Skipped, Assert.Single(receipt.Outcomes).Code);
        Assert.Equal(0, system.ApplyCalls);
        Assert.Equal([10, 20], (await system.CaptureAsync()).Windows.Select(window => (int)window.NormalBounds.X));
    }

    [Fact]
    public async Task RejectedMoveProducesPartialReceiptAndStillUndoesSuccessfulMoves()
    {
        DisplaySnapshot display = Display("display", 0, 0, 1920, 1040, 1, primary: true);
        LayoutProfile profile = Profile([display],
            Window("a-will-move", "com.example.a", new DesktopRect(900, 700, 800, 500), WindowState.Normal, "display"),
            Window("b-will-reject", "com.example.b", new DesktopRect(100, 100, 700, 450), WindowState.Normal, "display"),
            Window("c-also-move", "com.example.c", new DesktopRect(1600, 900, 300, 120), WindowState.Normal, "display"));
        CurrentDesktop current = Desktop([display],
            Window("a-now", "com.example.a", new DesktopRect(10, 10, 640, 400), WindowState.Normal, "display"),
            Window("b-now", "com.example.b", new DesktopRect(20, 20, 640, 400), WindowState.Normal, "display"),
            Window("c-now", "com.example.c", new DesktopRect(30, 30, 640, 400), WindowState.Normal, "display"));

        RestorePlan plan = await new RestorePlanner().CreatePlanAsync(profile, current);
        Assert.Equal(3, plan.Items.Length);

        InMemoryWindowSystem system = new(current);
        // The adapter refuses one move (rejected/failed) while the others succeed.
        Func<RestorePlanItem, WindowOutcome?> overrideFor = item => item.SavedWindowId == "b-will-reject"
            ? new(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Failed, "The application rejected the move.")
            : null;
        system.OutcomeOverride = item => overrideFor(item)!;

        RestoreCoordinator coordinator = new(system);
        UndoReceipt receipt = await coordinator.ApplyAsync(plan);
        Assert.Equal(
            new Dictionary<string, WindowOutcomeCode>
            {
                ["a-will-move"] = WindowOutcomeCode.Succeeded,
                ["b-will-reject"] = WindowOutcomeCode.Failed,
                ["c-also-move"] = WindowOutcomeCode.Succeeded,
            },
            receipt.Outcomes.ToDictionary(outcome => outcome.SavedWindowId, outcome => outcome.Code));

        // Undo remains available and rolls back the two successful moves. The rejected window is
        // replayed best-effort by undo and fails again exactly as the real adapter would reject it.
        ImmutableArray<WindowOutcome> undone = await coordinator.UndoAsync(receipt);
        Assert.Equal(
            new Dictionary<string, WindowOutcomeCode>
            {
                ["a-will-move"] = WindowOutcomeCode.Succeeded,
                ["b-will-reject"] = WindowOutcomeCode.Failed,
                ["c-also-move"] = WindowOutcomeCode.Succeeded,
            },
            undone.ToDictionary(outcome => outcome.SavedWindowId, outcome => outcome.Code));
        CurrentDesktop afterUndo = await system.CaptureAsync();
        Assert.Equal([10, 20, 30], afterUndo.Windows.Select(window => (int)window.NormalBounds.X).OrderBy(x => x));
    }

    private static LayoutProfile Profile(ImmutableArray<DisplaySnapshot> displays, params WindowSnapshot[] windows) =>
        new(LayoutProfile.CurrentSchemaVersion, "scenario", DateTimeOffset.UnixEpoch, displays, windows.ToImmutableArray(), new(false));

    private static CurrentDesktop Desktop(ImmutableArray<DisplaySnapshot> displays, params WindowSnapshot[] windows) =>
        new(DateTimeOffset.UnixEpoch, displays, windows.ToImmutableArray());

    private static DisplaySnapshot Display(string id, double x, double y, double width, double height, double scale, bool primary,
        DisplayOrientation orientation = DisplayOrientation.Landscape) =>
        new(id, id, new DesktopRect(x, y, width, height + 40), new DesktopRect(x, y, width, height), scale, orientation, primary);

    private static WindowSnapshot Window(string id, string app, DesktopRect bounds, WindowState state, string display) =>
        new(id, new ApplicationIdentity(app), null, null, bounds, state, display);
}
