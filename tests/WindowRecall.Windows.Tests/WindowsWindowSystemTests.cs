using System.Collections.Immutable;
using WindowRecall.Core;
using WindowRecall.Platform.Windows;

namespace WindowRecall.Windows.Tests;

public sealed class WindowsWindowSystemTests
{
    [Fact]
    public async Task Capture_FiltersUnsupportedWindows_AndUsesLogicalNormalBounds()
    {
        FakeNativeApi native = new()
        {
            Monitors =
            [
                new(7, "DISPLAY1", new(0, 0, 3840, 2160), new(0, 0, 3840, 2080), 192, true),
            ],
        };
        native.AddWindow(10, Window("Editor", normalBounds: new(200, 100, 1600, 1200), state: NativeWindowState.Maximized));
        native.AddWindow(11, Window("Hidden") with { IsVisible = false });
        native.AddWindow(12, Window("Cloaked") with { IsCloaked = true });
        native.AddWindow(13, Window("Tool") with { IsToolWindow = true });
        native.AddWindow(14, Window("Owned") with { Owner = 99 });
        native.AddWindow(15, Window("Shell"));
        native.ShellWindow = 15;
        native.AddWindow(16, NativeResult<NativeWindowInfo>.Failure(NativeError.AccessDenied, "protected process"));

        WindowsWindowSystem system = new(native);
        CurrentDesktop desktop = await system.CaptureAsync();

        WindowSnapshot captured = Assert.Single(desktop.Windows);
        Assert.Equal("Editor", captured.Title);
        Assert.Equal(WindowState.Maximized, captured.State);
        Assert.Equal(new DesktopRect(100, 50, 800, 600), captured.NormalBounds);
        Assert.Equal("monitor-7", captured.DisplayId);
        Assert.NotEqual("10", captured.WindowId);
        Assert.StartsWith("window-", captured.WindowId, StringComparison.Ordinal);
        Assert.Equal(new DesktopRect(0, 0, 1920, 1040), Assert.Single(desktop.Displays).WorkArea);
    }

    [Fact]
    public async Task Capture_IgnoresInvalidAndDuplicateNativeMonitors()
    {
        FakeNativeApi native = new()
        {
            Monitors =
            [
                new(1, "valid", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, true),
                new(1, "duplicate", new(0, 0, 1, 1), new(0, 0, 1, 1), 96, false),
                new(2, "zero-dpi", new(0, 0, 100, 100), new(0, 0, 100, 100), 0, false),
                new(3, "negative", new(0, 0, -1, 100), new(0, 0, -1, 100), 96, false),
            ],
        };

        CurrentDesktop desktop = await new WindowsWindowSystem(native).CaptureAsync();

        Assert.Equal("monitor-1", Assert.Single(desktop.Displays).Id);
    }

    [Fact]
    public async Task Capture_MixedDpiCoordinatesScaleRelativeToMonitorOrigin()
    {
        FakeNativeApi native = new()
        {
            Monitors =
            [
                new(1, "primary", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, true),
                new(2, "right", new(1920, 0, 2560, 1440), new(1920, 0, 2560, 1400), 192, false),
            ],
        };
        native.AddWindow(10, Window("Editor", new NativeRect(2120, 100, 1600, 1200)) with { MonitorId = 2 });

        CurrentDesktop desktop = await new WindowsWindowSystem(native).CaptureAsync();

        DisplaySnapshot right = Assert.Single(desktop.Displays, display => display.Id == "monitor-2");
        Assert.Equal(new DesktopRect(1920, 0, 1280, 720), right.Bounds);
        Assert.Equal(new DesktopRect(2020, 50, 800, 600), Assert.Single(desktop.Windows).NormalBounds);
    }

    [Fact]
    public void CoordinateHelper_ConvertsWorkspaceNormalPositionToScreenCoordinates()
    {
        NativeRect screen = WindowsCoordinateConverter.WorkspaceToScreen(
            new NativeRect(10, 20, 800, 600),
            new NativeRect(1920, 0, 1920, 1080),
            new NativeRect(1920, 40, 1920, 1040));

        Assert.Equal(new NativeRect(10, 60, 800, 600), screen);
    }

    [Fact]
    public async Task Apply_ApprovedMoveAndState_ReobservesWithGeometryTolerance()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        RestorePlan plan = Plan(new RestorePlanItem("saved-editor", current.WindowId,
            RestoreAction.MoveResize, new DesktopRect(50, 60, 500, 400), WindowState.Maximized, "approved preview"));
        native.AfterPosition = new NativeRect(101, 119, 1002, 801); // <= 1 logical px at 200% DPI

        ImmutableArray<WindowOutcome> outcomes = await system.ApplyAsync(plan, ["saved-editor"]);

        Assert.Equal(WindowOutcomeCode.Succeeded, Assert.Single(outcomes).Code);
        Assert.Equal(new NativeRect(100, 120, 1000, 800), Assert.Single(native.PositionCalls).Bounds);
        Assert.Equal(NativeWindowState.Maximized, Assert.Single(native.ShowCalls).State);
        Assert.True(native.ObserveCount >= 2);
    }

    [Fact]
    public async Task Apply_DuplicateAndMissingApprovals_DoNotMoveWindows()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        RestorePlan plan = Plan(
            new RestorePlanItem("duplicate", current.WindowId, RestoreAction.MoveResize, new(1, 2, 300, 200), null, "preview"),
            new RestorePlanItem("missing", current.WindowId, RestoreAction.MoveResize, new(3, 4, 300, 200), null, "preview"));

        ImmutableArray<WindowOutcome> outcomes = await system.ApplyAsync(plan, ["duplicate", "duplicate"]);

        Assert.Collection(outcomes,
            duplicate => Assert.Equal(WindowOutcomeCode.Failed, duplicate.Code),
            missing => Assert.Equal(WindowOutcomeCode.Skipped, missing.Code));
        Assert.Empty(native.PositionCalls);
    }

    [Fact]
    public async Task Apply_FullScreenState_IsUnsupportedAndNeverSentToWin32()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        RestorePlan plan = Plan(new RestorePlanItem("full-screen", current.WindowId,
            RestoreAction.ChangeState, null, WindowState.FullScreen, "preview"));

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(plan, ["full-screen"]));

        Assert.Equal(WindowOutcomeCode.Unsupported, outcome.Code);
        Assert.Empty(native.ShowCalls);
    }

    [Fact]
    public async Task Apply_BoundsChangeWithMultipleValidMonitors_IsUnsupported_ButStateOnlyIsAllowed()
    {
        FakeNativeApi native = OneWindowDesktop();
        native.Monitors =
        [
            .. native.Monitors,
            new(8, "DISPLAY2", new(3840, 0, 1920, 1080), new(3840, 0, 1920, 1040), 96, false),
        ];
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        RestorePlan plan = Plan(
            new RestorePlanItem("move", current.WindowId, RestoreAction.MoveResize, new(20, 30, 400, 300), null, "preview"),
            new RestorePlanItem("state", current.WindowId, RestoreAction.ChangeState, null, WindowState.Maximized, "preview"));

        ImmutableArray<WindowOutcome> outcomes = await system.ApplyAsync(plan, ["move", "state"]);

        Assert.Equal(WindowOutcomeCode.Unsupported, outcomes[0].Code);
        Assert.Equal(WindowOutcomeCode.Succeeded, outcomes[1].Code);
        Assert.Empty(native.PositionCalls);
        Assert.Single(native.ShowCalls);
    }

    [Fact]
    public async Task Apply_StateOnlyNormalTarget_RestoresMaximizedWindow()
    {
        FakeNativeApi native = OneWindowDesktop(NativeWindowState.Maximized);
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        RestorePlan plan = Plan(new RestorePlanItem("restore", current.WindowId,
            RestoreAction.ChangeState, null, WindowState.Normal, "preview"));

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(plan, ["restore"]));

        Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code);
        Assert.Equal(["show:Normal"], native.CallOrder);
    }

    [Fact]
    public async Task Apply_MoveOnlyOnMaximizedWindow_PreservesExistingState()
    {
        FakeNativeApi native = OneWindowDesktop(NativeWindowState.Maximized);
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        RestorePlan plan = Plan(new RestorePlanItem("move", current.WindowId,
            RestoreAction.MoveResize, new(20, 30, 400, 300), null, "preview"));

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(plan, ["move"]));

        Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code);
        Assert.Equal(["show:Normal", "position", "show:Maximized"], native.CallOrder);
    }

    [Fact]
    public async Task Apply_StatefulWindow_RestoresBeforePositionThenAppliesTargetState()
    {
        FakeNativeApi native = OneWindowDesktop(NativeWindowState.Maximized);
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        RestorePlan plan = Plan(new RestorePlanItem("stateful", current.WindowId,
            RestoreAction.MoveResize, new(20, 30, 400, 300), WindowState.Maximized, "preview"));

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(plan, ["stateful"]));

        Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code);
        Assert.Equal(["show:Normal", "position", "show:Maximized"], native.CallOrder);
    }

    [Fact]
    public async Task Apply_MapsVanishedProtectedRejectedAndUnverifiedWindows()
    {
        await AssertApplyError(NativeResult<NativeWindowInfo>.Failure(NativeError.NotFound, "vanished"), null, WindowOutcomeCode.NotFound);
        await AssertApplyError(NativeResult<NativeWindowInfo>.Failure(NativeError.AccessDenied, "protected"), null, WindowOutcomeCode.PermissionDenied);
        await AssertApplyError(null, NativeResult<bool>.Failure(NativeError.Rejected, "rejected"), WindowOutcomeCode.Failed);

        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        native.AfterPosition = new NativeRect(900, 900, 100, 100);
        WindowOutcome mismatch = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("mismatch", current.WindowId,
            RestoreAction.MoveResize, new(10, 10, 200, 100), null, "preview")), ["mismatch"]));
        Assert.Equal(WindowOutcomeCode.Failed, mismatch.Code);
    }

    [Fact]
    public async Task Apply_PreCancelled_ReturnsPerItemCancelledWithoutNativeCalls()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("cancelled", current.WindowId,
            RestoreAction.MoveResize, new(1, 1, 100, 100), null, "preview")), ["cancelled"], cancellation.Token));

        Assert.Equal(WindowOutcomeCode.Cancelled, outcome.Code);
        Assert.Empty(native.PositionCalls);
    }

    [Fact]
    public async Task Apply_RetriesFiniteVerificationWhenFirstObservationIsStale()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        native.StaleObservationsAfterMutation = 1;

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("state", current.WindowId,
            RestoreAction.ChangeState, null, WindowState.Maximized, "preview")), ["state"]));

        Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code);
        Assert.Equal(4, native.ObserveCount); // capture, identity revalidation, stale verification, converged verification
    }

    [Fact]
    public async Task Capture_CancelledReplacementKeepsPreviousCompleteSessionMap()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot original = Assert.Single((await system.CaptureAsync()).Windows);
        native.AddWindow(11, Window("Second"));
        using CancellationTokenSource cancellation = new();
        native.OnObserve = count => { if (count == 2) cancellation.Cancel(); };

        await Assert.ThrowsAsync<OperationCanceledException>(() => system.CaptureAsync(cancellation.Token));
        native.OnObserve = null;
        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("state", original.WindowId,
            RestoreAction.ChangeState, null, WindowState.Maximized, "preview")), ["state"]));

        Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code);
    }

    [Fact]
    public async Task Capabilities_DiscloseOneMonitorBoundsApplyLimitation()
    {
        WindowSystemCapabilities capabilities = await new WindowsWindowSystem(OneWindowDesktop()).GetCapabilitiesAsync();

        Assert.Contains("one", capabilities.Limitation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("monitor", capabilities.Limitation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_ReusedHandleWithDifferentProcessIdentity_IsNotMutated()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        native.ReplaceIdentityOnNextObservation = true;

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("move", current.WindowId,
            RestoreAction.MoveResize, new(10, 10, 200, 100), null, "preview")), ["move"]));

        Assert.Equal(WindowOutcomeCode.NotFound, outcome.Code);
        Assert.Empty(native.PositionCalls);
    }

    [Fact]
    public async Task Apply_CancellationImmediatelyBeforeMutation_DoesNotStartItem()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        using CancellationTokenSource cancellation = new();
        native.OnObserve = count => { if (count == 2) cancellation.Cancel(); };

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("move", current.WindowId,
            RestoreAction.MoveResize, new(10, 10, 200, 100), null, "preview")), ["move"], cancellation.Token));

        Assert.Equal(WindowOutcomeCode.Cancelled, outcome.Code);
        Assert.Empty(native.PositionCalls);
    }

    [Fact]
    public async Task Apply_CancellationAfterMutationFinishesCurrentItemAndCancelsNextItem()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        using CancellationTokenSource cancellation = new();
        native.OnShow = cancellation.Cancel;
        RestorePlan plan = Plan(
            new RestorePlanItem("first", current.WindowId, RestoreAction.ChangeState, null, WindowState.Maximized, "preview"),
            new RestorePlanItem("second", current.WindowId, RestoreAction.ChangeState, null, WindowState.Normal, "preview"));

        ImmutableArray<WindowOutcome> outcomes = await system.ApplyAsync(plan, ["first", "second"], cancellation.Token);

        Assert.Equal(WindowOutcomeCode.Succeeded, outcomes[0].Code);
        Assert.Equal(WindowOutcomeCode.Cancelled, outcomes[1].Code);
        Assert.Single(native.ShowCalls);
    }

    [Fact]
    public async Task Apply_DuplicateSavedWindowIdsInPlan_AreRejectedWithoutMutation()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        RestorePlan plan = Plan(
            new RestorePlanItem("same", current.WindowId, RestoreAction.MoveResize, new(1, 2, 300, 200), null, "preview"),
            new RestorePlanItem("same", current.WindowId, RestoreAction.ChangeState, null, WindowState.Maximized, "preview"));

        ImmutableArray<WindowOutcome> outcomes = await system.ApplyAsync(plan, ["same"]);

        Assert.All(outcomes, outcome => Assert.Equal(WindowOutcomeCode.Failed, outcome.Code));
        Assert.Empty(native.PositionCalls);
        Assert.Empty(native.ShowCalls);
    }

    [Theory]
    [InlineData(double.NaN, 0, 100, 100)]
    [InlineData(double.PositiveInfinity, 0, 100, 100)]
    [InlineData(0, double.NegativeInfinity, 100, 100)]
    [InlineData(double.MaxValue, 0, 100, 100)]
    [InlineData(0, 0, 0, 100)]
    [InlineData(0, 0, -1, 100)]
    [InlineData(0, 0, 100, -1)]
    public async Task Apply_InvalidMoveBounds_AreRejectedBeforeAnyNativeMutation(
        double x, double y, double width, double height)
    {
        FakeNativeApi native = OneWindowDesktop(NativeWindowState.Maximized);
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("invalid", current.WindowId,
            RestoreAction.MoveResize, new(x, y, width, height), null, "preview")), ["invalid"]));

        Assert.Equal(WindowOutcomeCode.Unsupported, outcome.Code);
        Assert.Empty(native.PositionCalls);
        Assert.Empty(native.ShowCalls);
    }

    [Theory]
    [InlineData(RestoreAction.MoveResize, false, false)]
    [InlineData(RestoreAction.ChangeState, false, false)]
    [InlineData(RestoreAction.ChangeState, true, true)]
    public async Task Apply_ActionTargetMismatch_IsRejectedWithoutNativeMutation(
        RestoreAction action, bool hasBounds, bool hasState)
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        DesktopRect? bounds = hasBounds ? new(1, 2, 300, 200) : null;
        WindowState? state = hasState ? WindowState.Maximized : null;

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("mismatch", current.WindowId,
            action, bounds, state, "preview")), ["mismatch"]));

        Assert.Equal(WindowOutcomeCode.Unsupported, outcome.Code);
        Assert.Empty(native.PositionCalls);
        Assert.Empty(native.ShowCalls);
    }

    [Fact]
    public async Task Apply_RestoreSuccessThenMoveFailure_StopsAndReportsPartialStateChange()
    {
        FakeNativeApi native = OneWindowDesktop(NativeWindowState.Maximized);
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        native.PositionResult = NativeResult<bool>.Failure(NativeError.Rejected, "move rejected");

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("partial", current.WindowId,
            RestoreAction.MoveResize, new(1, 2, 300, 200), null, "preview")), ["partial"]));

        Assert.Equal(WindowOutcomeCode.Failed, outcome.Code);
        Assert.Contains("Partial", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Normal", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(["show:Normal", "position"], native.CallOrder);
    }

    [Fact]
    public async Task Apply_MoveSuccessThenStateFailure_StopsAndReportsPartialBoundsChange()
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        native.ShowResult = NativeResult<bool>.Failure(NativeError.Rejected, "state rejected");

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("partial", current.WindowId,
            RestoreAction.MoveResize, new(1, 2, 300, 200), WindowState.Maximized, "preview")), ["partial"]));

        Assert.Equal(WindowOutcomeCode.Failed, outcome.Code);
        Assert.Contains("Partial", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("bounds", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["position", "show:Maximized"], native.CallOrder);
    }

    [Fact]
    public async Task Apply_RestoreFailure_DoesNotRetryNormalState()
    {
        FakeNativeApi native = OneWindowDesktop(NativeWindowState.Maximized);
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        native.ShowResult = NativeResult<bool>.Failure(NativeError.Rejected, "restore rejected");

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("failed", current.WindowId,
            RestoreAction.MoveResize, new(1, 2, 300, 200), WindowState.Normal, "preview")), ["failed"]));

        Assert.Equal(WindowOutcomeCode.Failed, outcome.Code);
        Assert.Equal(["show:Normal"], native.CallOrder);
        Assert.Empty(native.PositionCalls);
    }

    private static async Task AssertApplyError(
        NativeResult<NativeWindowInfo>? observationError,
        NativeResult<bool>? positionError,
        WindowOutcomeCode expected)
    {
        FakeNativeApi native = OneWindowDesktop();
        WindowsWindowSystem system = new(native);
        WindowSnapshot current = Assert.Single((await system.CaptureAsync()).Windows);
        if (observationError is { } observation) native.AddWindow(10, observation);
        if (positionError is { } position) native.PositionResult = position;
        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(new RestorePlanItem("error", current.WindowId,
            RestoreAction.MoveResize, new(1, 1, 100, 100), null, "preview")), ["error"]));
        Assert.Equal(expected, outcome.Code);
    }

    private static FakeNativeApi OneWindowDesktop(NativeWindowState state = NativeWindowState.Normal)
    {
        FakeNativeApi native = new()
        {
            Monitors = [new(7, "DISPLAY1", new(0, 0, 3840, 2160), new(0, 0, 3840, 2080), 192, true)],
        };
        native.AddWindow(10, Window("Editor", state: state));
        return native;
    }

    private static RestorePlan Plan(params RestorePlanItem[] items) =>
        new(Guid.NewGuid(), [.. items], new CurrentDesktop(DateTimeOffset.UtcNow, [], []));

    private static NativeWindowInfo Window(
        string title,
        NativeRect? normalBounds = null,
        NativeWindowState state = NativeWindowState.Normal) =>
        new(true, false, false, 0, 123, title, "main", "example.app", @"C:\example.exe",
            normalBounds ?? new NativeRect(0, 0, 800, 600), state, 7);

    private sealed class FakeNativeApi : IWindowsNativeApi
    {
        private readonly Dictionary<nint, NativeResult<NativeWindowInfo>> windows = new();

        public bool IsSupported { get; set; } = true;
        public nint ShellWindow { get; set; }
        public IReadOnlyList<NativeMonitorInfo> Monitors { get; set; } = [];
        public NativeRect? AfterPosition { get; set; }
        public List<(nint Handle, NativeRect Bounds)> PositionCalls { get; } = [];
        public List<(nint Handle, NativeWindowState State)> ShowCalls { get; } = [];
        public int ObserveCount { get; private set; }
        public List<string> CallOrder { get; } = [];
        public NativeResult<bool>? PositionResult { get; set; }
        public NativeResult<bool>? ShowResult { get; set; }
        public int StaleObservationsAfterMutation { get; set; }
        public bool ReplaceIdentityOnNextObservation { get; set; }
        public Action<int>? OnObserve { get; set; }
        public Action? OnShow { get; set; }
        private NativeWindowInfo? staleWindow;

        public void AddWindow(nint handle, NativeWindowInfo window) => windows[handle] = NativeResult<NativeWindowInfo>.Success(window);
        public void AddWindow(nint handle, NativeResult<NativeWindowInfo> result) => windows[handle] = result;
        public NativeResult<IReadOnlyList<nint>> EnumerateTopLevelWindows() => NativeResult<IReadOnlyList<nint>>.Success(windows.Keys.ToArray());
        public NativeResult<IReadOnlyList<NativeMonitorInfo>> EnumerateMonitors() => NativeResult<IReadOnlyList<NativeMonitorInfo>>.Success(Monitors);
        public NativeResult<nint> GetShellWindow() => NativeResult<nint>.Success(ShellWindow);
        public NativeResult<NativeWindowInfo> ObserveWindow(nint handle)
        {
            ObserveCount++;
            OnObserve?.Invoke(ObserveCount);
            if (ReplaceIdentityOnNextObservation)
            {
                ReplaceIdentityOnNextObservation = false;
                NativeWindowInfo reused = windows[handle].Value! with { ProcessId = 999, ApplicationId = "unrelated.app" };
                windows[handle] = NativeResult<NativeWindowInfo>.Success(reused);
            }
            if (staleWindow is not null && StaleObservationsAfterMutation-- > 0)
                return NativeResult<NativeWindowInfo>.Success(staleWindow);
            return windows[handle];
        }
        public NativeResult<bool> SetWindowPosition(nint handle, NativeRect bounds)
        {
            PositionCalls.Add((handle, bounds));
            CallOrder.Add("position");
            if (PositionResult is { } result) return result;
            NativeWindowInfo current = windows[handle].Value!;
            staleWindow = current;
            windows[handle] = NativeResult<NativeWindowInfo>.Success(current with { NormalBounds = AfterPosition ?? bounds });
            return NativeResult<bool>.Success(true);
        }
        public NativeResult<bool> ShowWindow(nint handle, NativeWindowState state)
        {
            ShowCalls.Add((handle, state));
            CallOrder.Add($"show:{state}");
            OnShow?.Invoke();
            if (ShowResult is { } result) return result;
            NativeWindowInfo current = windows[handle].Value!;
            staleWindow = current;
            windows[handle] = NativeResult<NativeWindowInfo>.Success(current with { State = state });
            return NativeResult<bool>.Success(true);
        }
    }
}
