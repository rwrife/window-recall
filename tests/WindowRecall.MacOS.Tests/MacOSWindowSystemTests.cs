using System.Collections.Immutable;
using WindowRecall.Core;
using WindowRecall.Platform.MacOS;

namespace WindowRecall.MacOS.Tests;

public sealed class MacOSWindowSystemTests
{
    [Fact]
    public async Task Capture_UsesRandomOpaqueIds_ThatDoNotExposeNativeWindowIds()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);

        string first = Assert.Single((await system.CaptureAsync()).Windows).WindowId;
        string second = Assert.Single((await system.CaptureAsync()).Windows).WindowId;

        Assert.StartsWith("window-", first, StringComparison.Ordinal);
        Assert.NotEqual("cg:10", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task PermissionAbsent_ReportsReadOnlyCapabilities_AndCapturesEligibleCoreGraphicsWindows()
    {
        FakeMacOSNativeApi native = new()
        {
            IsAccessibilityTrusted = false,
            Displays = [Display()],
            Windows =
            [
                Window(10, bundleId: "org.example.Editor", executableUrl: "file:///Applications/Editor.app/Contents/MacOS/Editor"),
                Window(11, ownerPid: 0),
                Window(12, isOnScreen: false),
                Window(13, layer: 2),
            ],
        };
        MacOSWindowSystem system = new(native);

        WindowSystemCapabilities capabilities = await system.GetCapabilitiesAsync();
        CurrentDesktop desktop = await system.CaptureAsync();

        Assert.True(capabilities.CanObserve);
        Assert.False(capabilities.CanMoveResize);
        Assert.False(capabilities.CanChangeState);
        Assert.Contains("Accessibility", capabilities.Limitation, StringComparison.Ordinal);
        Assert.Equal(2, desktop.Windows.Length);
        WindowSnapshot captured = Assert.Single(desktop.Windows, window => window.Application.ApplicationId == "org.example.Editor");
        Assert.Equal("org.example.Editor", captured.Application.ApplicationId);
        Assert.Equal("/Applications/Editor.app/Contents/MacOS/Editor", captured.Application.ExecutablePath);
        Assert.Null(captured.Title);
        WindowSnapshot offScreen = Assert.Single(desktop.Windows, window => window.Application.ApplicationId == "org.example.App");
        Assert.Contains("restore-skip=", offScreen.Role, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ReusedNativeIdWithDifferentCapturedIdentity_IsNotMutated()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);
        WindowSnapshot captured = Assert.Single((await system.CaptureAsync()).Windows);
        native.Windows = [native.Windows[0] with { OwnerPid = 99 }];

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(captured.WindowId, Bounds()), ["saved"]));

        Assert.Equal(WindowOutcomeCode.NotFound, outcome.Code);
        Assert.Equal(0, native.SetBoundsCount);
    }

    [Fact]
    public async Task Capture_CancelledReplacementPreservesPriorCompleteSession()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);
        WindowSnapshot captured = Assert.Single((await system.CaptureAsync()).Windows);
        using CancellationTokenSource cancellation = new();
        native.OnObserveWindows = cancellation.Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(() => system.CaptureAsync(cancellation.Token));
        native.OnObserveWindows = null;
        native.AfterBounds = Bounds();
        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(captured.WindowId, Bounds()), ["saved"]));

        Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code);
    }

    [Fact]
    public async Task Apply_DuplicateApprovalsAndSavedIds_AreRejectedWithoutMutation()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);
        string id = Assert.Single((await system.CaptureAsync()).Windows).WindowId;
        RestorePlan duplicatePlan = new(Guid.NewGuid(),
        [
            new("same", id, RestoreAction.MoveResize, Bounds(), null, "test"),
            new("same", id, RestoreAction.ChangeState, null, WindowState.Minimized, "test"),
        ], new CurrentDesktop(DateTimeOffset.UtcNow, [], []));

        ImmutableArray<WindowOutcome> duplicateIds = await system.ApplyAsync(duplicatePlan, ["same"]);
        WindowOutcome duplicateApproval = Assert.Single(await system.ApplyAsync(Plan(id, Bounds()), ["saved", "saved"]));

        Assert.All(duplicateIds, outcome => Assert.Equal(WindowOutcomeCode.Failed, outcome.Code));
        Assert.Equal(WindowOutcomeCode.Failed, duplicateApproval.Code);
        Assert.Equal(0, native.SetBoundsCount);
        Assert.Equal(0, native.SetStateCount);
    }

    [Fact]
    public async Task Apply_DuplicateCurrentWindowIds_AreRejectedWithoutMutation()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);
        string id = Assert.Single((await system.CaptureAsync()).Windows).WindowId;
        RestorePlan plan = new(Guid.NewGuid(),
        [
            new("first", id, RestoreAction.MoveResize, Bounds(), null, "test"),
            new("second", id, RestoreAction.ChangeState, null, WindowState.Minimized, "test"),
        ], new CurrentDesktop(DateTimeOffset.UtcNow, [], []));

        ImmutableArray<WindowOutcome> outcomes = await system.ApplyAsync(plan, ["first", "second"]);

        Assert.All(outcomes, outcome => Assert.Equal(WindowOutcomeCode.Failed, outcome.Code));
        Assert.Equal(0, native.SetBoundsCount);
        Assert.Equal(0, native.SetStateCount);
    }

    [Fact]
    public async Task Apply_MoveResizeWithSupportedState_AppliesAndVerifiesBoth()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);
        string id = Assert.Single((await system.CaptureAsync()).Windows).WindowId;
        native.AfterBounds = Bounds();
        native.AfterState = WindowState.Minimized;
        RestorePlan plan = new(Guid.NewGuid(),
            [new RestorePlanItem("saved", id, RestoreAction.MoveResize, Bounds(), WindowState.Minimized, "test")],
            new CurrentDesktop(DateTimeOffset.UtcNow, [], []));

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(plan, ["saved"]));

        Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code);
        Assert.Equal(1, native.SetBoundsCount);
        Assert.Equal(1, native.SetStateCount);
    }

    [Fact]
    public async Task Capture_ReportsUnsupportedObservedWindowsWithRestoreSkipRoles()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        native.Windows =
        [
            Window(10),
            Window(11) with { IsModal = true },
            Window(12) with { IsFullScreen = true, State = WindowState.FullScreen },
            Window(13) with { IsSystemOwned = true },
            Window(14) with { SupportsSize = false },
        ];

        CurrentDesktop desktop = await new MacOSWindowSystem(native).CaptureAsync();

        Assert.Equal(5, desktop.Windows.Length);
        Assert.Single(desktop.Windows, window => window.Role == "AXWindow");
        Assert.Equal(4, desktop.Windows.Count(window => window.Role?.Contains("restore-skip=", StringComparison.Ordinal) == true));
    }

    [Theory]
    [InlineData(double.NaN, 0, 100, 100)]
    [InlineData(double.PositiveInfinity, 0, 100, 100)]
    [InlineData(0, double.NegativeInfinity, 100, 100)]
    [InlineData(0, 0, 0, 100)]
    [InlineData(0, 0, -1, 100)]
    public async Task Apply_MalformedGeometry_IsRejectedWithoutMutation(double x, double y, double width, double height)
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);
        string id = Assert.Single((await system.CaptureAsync()).Windows).WindowId;

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(id, new(x, y, width, height)), ["saved"]));

        Assert.Equal(WindowOutcomeCode.Unsupported, outcome.Code);
        Assert.Equal(0, native.SetBoundsCount);
    }

    [Theory]
    [InlineData(RestoreAction.MoveResize, false, false)]
    [InlineData(RestoreAction.ChangeState, true, true)]
    [InlineData(RestoreAction.Ambiguous, false, false)]
    public async Task Apply_ActionTargetMismatchOrAmbiguity_IsRejectedWithoutMutation(
        RestoreAction action, bool hasBounds, bool hasState)
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);
        string id = Assert.Single((await system.CaptureAsync()).Windows).WindowId;
        RestorePlan plan = new(Guid.NewGuid(), [new RestorePlanItem("saved", id, action,
            hasBounds ? Bounds() : null, hasState ? WindowState.Minimized : null, "test")],
            new CurrentDesktop(DateTimeOffset.UtcNow, [], []));

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(plan, ["saved"]));

        Assert.True(outcome.Code is WindowOutcomeCode.Unsupported or WindowOutcomeCode.Ambiguous);
        Assert.Equal(0, native.SetBoundsCount);
        Assert.Equal(0, native.SetStateCount);
    }

    [Theory]
    [InlineData(-25211, (int)MacOSNativeError.PermissionDenied)] // kAXErrorAPIDisabled
    [InlineData(-25202, (int)MacOSNativeError.StaleElement)] // kAXErrorInvalidUIElement
    [InlineData(-25203, (int)MacOSNativeError.StaleElement)] // kAXErrorInvalidUIElementObserver
    [InlineData(-25205, (int)MacOSNativeError.Unsupported)] // kAXErrorAttributeUnsupported
    [InlineData(-25208, (int)MacOSNativeError.Unsupported)] // kAXErrorNotImplemented
    [InlineData(-25212, (int)MacOSNativeError.Unsupported)] // kAXErrorNoValue
    [InlineData(-25204, (int)MacOSNativeError.Rejected)] // kAXErrorCannotComplete
    public void NativeAxErrors_PreservePermissionStaleUnsupportedAndRejectedMeaning(int nativeError, int expected)
    {
        Assert.Equal((MacOSNativeError)expected, MacOSNativeApi.MapAxError(nativeError));
    }

    [Theory]
    [InlineData((int)MacOSNativeError.PermissionDenied, WindowOutcomeCode.PermissionDenied)]
    [InlineData((int)MacOSNativeError.StaleElement, WindowOutcomeCode.NotFound)]
    [InlineData((int)MacOSNativeError.Rejected, WindowOutcomeCode.Failed)]
    [InlineData((int)MacOSNativeError.Unsupported, WindowOutcomeCode.Unsupported)]
    public async Task Apply_PreservesNativeErrorAndPartialDetail(int errorValue, WindowOutcomeCode expected)
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        native.BoundsResult = new MacOSMutationResult((MacOSNativeError)errorValue, "Partial apply: position changed; AXSize failed (-25211).");
        MacOSWindowSystem system = new(native);
        string id = Assert.Single((await system.CaptureAsync()).Windows).WindowId;

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(id, Bounds()), ["saved"]));

        Assert.Equal(expected, outcome.Code);
        Assert.Contains("Partial apply", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("AXSize", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_CancellationImmediatelyBeforeNativeMutation_DoesNotMutate()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);
        string id = Assert.Single((await system.CaptureAsync()).Windows).WindowId;
        using CancellationTokenSource cancellation = new();
        native.OnReobserve = count => { if (count == 1) cancellation.Cancel(); };

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(id, Bounds()), ["saved"], cancellation.Token));

        Assert.Equal(WindowOutcomeCode.Cancelled, outcome.Code);
        Assert.Equal(0, native.SetBoundsCount);
    }

    [Fact]
    public async Task Apply_AfterMutationCancellationFinishesCurrentVerificationAndCancelsNextItem()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        native.Windows = [Window(10), Window(11)];
        MacOSWindowSystem system = new(native);
        ImmutableArray<WindowSnapshot> captured = (await system.CaptureAsync()).Windows;
        using CancellationTokenSource cancellation = new();
        native.AfterBounds = Bounds();
        native.OnSetBounds = cancellation.Cancel;
        RestorePlan plan = new(Guid.NewGuid(),
        [
            new("first", captured[0].WindowId, RestoreAction.MoveResize, Bounds(), null, "test"),
            new("second", captured[1].WindowId, RestoreAction.MoveResize, Bounds(), null, "test"),
        ], new CurrentDesktop(DateTimeOffset.UtcNow, [], []));

        ImmutableArray<WindowOutcome> outcomes = await system.ApplyAsync(plan, ["first", "second"], cancellation.Token);

        Assert.Equal(WindowOutcomeCode.Succeeded, outcomes[0].Code);
        Assert.Equal(WindowOutcomeCode.Cancelled, outcomes[1].Code);
        Assert.Equal(1, native.SetBoundsCount);
    }

    [Fact]
    public async Task Apply_RetriesAndAcceptsGeometryWithinTwoLogicalPixels()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);
        string id = Assert.Single((await system.CaptureAsync()).Windows).WindowId;
        native.Reobservations.Enqueue(native.Windows[0]);
        native.Reobservations.Enqueue(native.Windows[0]);
        native.Reobservations.Enqueue(native.Windows[0] with { Bounds = new(151.9, 158.1, 701.9, 498.1) });

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(id, Bounds()), ["saved"]));

        Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code);
        Assert.Equal(3, native.ReobserveCount);
    }

    [Fact]
    public async Task Apply_SkipsModalWindowWithoutCallingMutation()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        MacOSWindowSystem system = new(native);
        string id = Assert.Single((await system.CaptureAsync()).Windows).WindowId;
        native.Windows = [native.Windows[0] with { IsModal = true }];

        WindowOutcome outcome = Assert.Single(await system.ApplyAsync(Plan(id, Bounds()), ["saved"]));

        Assert.Equal(WindowOutcomeCode.Skipped, outcome.Code);
        Assert.Equal(0, native.SetBoundsCount);
    }

    [Fact]
    public async Task PermissionGranted_CaptureReportsWindowsWithoutSupportedPositionAndSize()
    {
        FakeMacOSNativeApi native = TrustedWindowApi();
        native.Windows = [native.Windows[0] with { SupportsSize = false }];

        CurrentDesktop desktop = await new MacOSWindowSystem(native).CaptureAsync();

        WindowSnapshot window = Assert.Single(desktop.Windows);
        Assert.Contains("restore-skip=", window.Role, StringComparison.Ordinal);
        Assert.Contains("position and size", window.Role, StringComparison.OrdinalIgnoreCase);
    }

    private static FakeMacOSNativeApi TrustedWindowApi() => new()
    {
        IsAccessibilityTrusted = true,
        Displays = [Display()],
        Windows = [Window(10)],
    };

    private static DesktopRect Bounds() => new(150, 160, 700, 500);

    private static RestorePlan Plan(string currentWindowId, DesktopRect bounds) =>
        new(Guid.NewGuid(), [new RestorePlanItem("saved", currentWindowId, RestoreAction.MoveResize, bounds, null, "test")],
            new CurrentDesktop(DateTimeOffset.UtcNow, [], []));

    private static MacOSDisplayObservation Display() =>
        new(1, "Built-in", new DesktopRect(0, 0, 1440, 900), new DesktopRect(0, 24, 1440, 876), 2, DisplayOrientation.Landscape, true);

    private static MacOSWindowObservation Window(
        uint id,
        int ownerPid = 42,
        string? bundleId = "org.example.App",
        string? executableUrl = "file:///Applications/App.app/Contents/MacOS/App",
        bool isOnScreen = true,
        int layer = 0) =>
        new(id, ownerPid, bundleId, executableUrl, new DesktopRect(100, 100, 800, 600), isOnScreen, layer, false, false, false, false, false, true, true, WindowState.Normal, 1);
}

internal sealed class FakeMacOSNativeApi : IMacOSNativeApi
{
    public bool IsAccessibilityTrusted { get; set; }
    public ImmutableArray<MacOSDisplayObservation> Displays { get; set; } = [];
    public ImmutableArray<MacOSWindowObservation> Windows { get; set; } = [];
    public MacOSMutationResult BoundsResult { get; set; } = MacOSMutationResult.Success;
    public MacOSMutationResult StateResult { get; set; } = MacOSMutationResult.Success;
    public DesktopRect? AfterBounds { get; set; }
    public WindowState? AfterState { get; set; }
    public Queue<MacOSWindowObservation?> Reobservations { get; } = new();
    public Action? OnObserveWindows { get; set; }
    public Action<int>? OnReobserve { get; set; }
    public Action? OnSetBounds { get; set; }
    public int SetBoundsCount { get; private set; }
    public int SetStateCount { get; private set; }
    public int ReobserveCount { get; private set; }
    public bool OpenSettingsResult { get; set; }
    public int OpenSettingsCount { get; private set; }

    public bool GetAccessibilityTrust() => IsAccessibilityTrusted;
    public ImmutableArray<MacOSDisplayObservation> ObserveDisplays() => Displays;
    public ImmutableArray<MacOSWindowObservation> ObserveWindows(bool includeAccessibilityDetails)
    {
        OnObserveWindows?.Invoke();
        return Windows;
    }
    public MacOSMutationResult SetWindowBounds(
        MacOSWindowIdentity identity,
        DesktopRect bounds,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new(MacOSNativeError.Cancelled);
        SetBoundsCount++;
        if (AfterBounds is { } after)
        {
            int index = FindWindowIndex(identity.WindowId);
            if (index >= 0) Windows = Windows.SetItem(index, Windows[index] with { Bounds = after });
        }
        OnSetBounds?.Invoke();
        return BoundsResult;
    }
    public MacOSMutationResult SetWindowState(
        MacOSWindowIdentity identity,
        WindowState state,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new(MacOSNativeError.Cancelled);
        SetStateCount++;
        if (AfterState is { } after)
        {
            int index = FindWindowIndex(identity.WindowId);
            if (index >= 0)
            {
                Windows = Windows.SetItem(index, Windows[index] with
                {
                    State = after,
                    IsMinimized = after == WindowState.Minimized,
                });
            }
        }
        return StateResult;
    }

    private int FindWindowIndex(uint windowId)
    {
        for (int index = 0; index < Windows.Length; index++)
        {
            if (Windows[index].WindowId == windowId) return index;
        }
        return -1;
    }

    public MacOSWindowObservation? ReobserveWindow(uint windowId)
    {
        ReobserveCount++;
        OnReobserve?.Invoke(ReobserveCount);
        return Reobservations.Count > 0 ? Reobservations.Dequeue() : Windows.FirstOrDefault(window => window.WindowId == windowId);
    }
    public bool OpenAccessibilitySettings()
    {
        OpenSettingsCount++;
        return OpenSettingsResult;
    }
}
