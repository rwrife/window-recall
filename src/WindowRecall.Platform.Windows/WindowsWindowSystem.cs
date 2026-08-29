using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Platform.Windows;

public sealed class WindowsWindowSystem : IWindowSystem
{
    private const int VerificationAttempts = 3;
    private static readonly TimeSpan VerificationDelay = TimeSpan.FromMilliseconds(20);
    private readonly IWindowsNativeApi native;
    private ImmutableDictionary<string, CapturedWindow> capturedWindows = ImmutableDictionary<string, CapturedWindow>.Empty.WithComparers(StringComparer.Ordinal);

    public WindowsWindowSystem() : this(new Win32NativeApi()) { }

    public WindowsWindowSystem(IWindowsNativeApi native) =>
        this.native = native ?? throw new ArgumentNullException(nameof(native));

    public Task<CurrentDesktop> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupported();
        IReadOnlyList<NativeMonitorInfo> observedMonitors = Require(native.EnumerateMonitors(), "monitor enumeration");
        NativeMonitorInfo[] monitors = observedMonitors.Where(IsValidMonitor)
            .GroupBy(monitor => monitor.Id).Select(group => group.First()).ToArray();
        IReadOnlyList<nint> enumerated = Require(native.EnumerateTopLevelWindows(), "window enumeration");
        NativeResult<nint> shellResult = native.GetShellWindow();
        nint shell = shellResult.IsSuccess ? shellResult.Value : 0;
        ImmutableDictionary<string, CapturedWindow> previousCapturedWindows = Volatile.Read(ref capturedWindows);
        ImmutableDictionary<string, CapturedWindow>.Builder newCapturedWindows =
            ImmutableDictionary.CreateBuilder<string, CapturedWindow>(StringComparer.Ordinal);

        ImmutableArray<DisplaySnapshot> displays = monitors.Select(ToDisplay).ToImmutableArray();
        Dictionary<long, NativeMonitorInfo> monitorById = monitors.ToDictionary(m => m.Id);
        ImmutableArray<WindowSnapshot>.Builder windows = ImmutableArray.CreateBuilder<WindowSnapshot>();
        foreach (nint handle in enumerated.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (handle == 0 || handle == shell)
                continue;
            NativeResult<NativeWindowInfo> observed = native.ObserveWindow(handle);
            if (!observed.IsSuccess || observed.Value is not { } info || !IsOrdinaryUserWindow(info))
                continue;
            if (!monitorById.TryGetValue(info.MonitorId, out NativeMonitorInfo? monitor) || monitor.Dpi is 0)
                continue;

            string id = previousCapturedWindows
                .Where(pair => pair.Value.Handle == handle && pair.Value.ProcessId == info.ProcessId &&
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Value.ApplicationId, info.ApplicationId) &&
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Value.ExecutablePath, info.ExecutablePath))
                .Select(pair => pair.Key)
                .FirstOrDefault() ?? $"window-{Guid.NewGuid():N}";
            newCapturedWindows.Add(id, new CapturedWindow(handle, info.ProcessId, info.ApplicationId, info.ExecutablePath));
            windows.Add(new WindowSnapshot(id,
                new ApplicationIdentity(info.ApplicationId, info.ExecutablePath), info.Role,
                string.IsNullOrWhiteSpace(info.Title) ? null : info.Title,
                WindowsCoordinateConverter.ToLogical(info.NormalBounds, monitor.Bounds, monitor.Dpi), ToState(info.State),
                $"monitor-{monitor.Id}"));
        }

        Interlocked.Exchange(ref capturedWindows, newCapturedWindows.ToImmutable());
        return Task.FromResult(new CurrentDesktop(DateTimeOffset.UtcNow, displays, windows.ToImmutable()));
    }

    public async Task<ImmutableArray<WindowOutcome>> ApplyAsync(RestorePlan plan, ImmutableArray<string> approvedSavedWindowIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureSupported();
        HashSet<string> duplicateApprovals = approvedSavedWindowIds
            .GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        HashSet<string> approved = approvedSavedWindowIds.ToHashSet(StringComparer.Ordinal);
        HashSet<string> duplicatePlanIds = plan.Items.GroupBy(item => item.SavedWindowId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        ImmutableDictionary<string, CapturedWindow> session = Volatile.Read(ref capturedWindows);
        ImmutableArray<WindowOutcome>.Builder outcomes = ImmutableArray.CreateBuilder<WindowOutcome>(plan.Items.Length);
        foreach (RestorePlanItem item in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Cancelled, "Restore was cancelled before this item was applied."));
                continue;
            }
            if (duplicateApprovals.Contains(item.SavedWindowId))
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Failed, "The approval ID was supplied more than once."));
                continue;
            }
            if (duplicatePlanIds.Contains(item.SavedWindowId))
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Failed, "The restore plan contains duplicate saved-window IDs."));
                continue;
            }
            if (!approved.Contains(item.SavedWindowId))
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Skipped, "Plan item was not approved."));
                continue;
            }
            if (!item.CanAutoApply || item.Action is RestoreAction.Ambiguous)
            {
                outcomes.Add(Outcome(item, item.Action is RestoreAction.Ambiguous ? WindowOutcomeCode.Ambiguous : WindowOutcomeCode.Unsupported,
                    "Only approved, unambiguous move or state plan items can be applied."));
                continue;
            }
            if (!HasValidActionTargets(item, out string validationMessage))
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Unsupported, validationMessage));
                continue;
            }
            if (item.TargetState is WindowState.FullScreen)
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Unsupported, "Generic Win32 full-screen transitions are not supported."));
                continue;
            }
            if (item.CurrentWindowId is null || !session.TryGetValue(item.CurrentWindowId, out CapturedWindow? captured))
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.NotFound, "The captured window session ID is missing or stale."));
                continue;
            }

            NativeMonitorInfo[] validMonitors = GetValidMonitors();

            NativeResult<NativeWindowInfo> beforeResult = native.ObserveWindow(captured.Handle);
            if (!beforeResult.IsSuccess || beforeResult.Value is not { } before)
            {
                outcomes.Add(FromNativeError(item, beforeResult.Error, beforeResult.Message, "Window vanished before apply."));
                continue;
            }
            if (before.ProcessId != captured.ProcessId ||
                !StringComparer.OrdinalIgnoreCase.Equals(before.ApplicationId, captured.ApplicationId) ||
                !StringComparer.OrdinalIgnoreCase.Equals(before.ExecutablePath, captured.ExecutablePath))
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.NotFound,
                    "The native handle no longer belongs to the application captured for this session."));
                continue;
            }
            NativeMonitorInfo? monitor = item.TargetBounds is { } targetBounds
                ? SelectTargetMonitor(validMonitors, targetBounds)
                : validMonitors.FirstOrDefault(candidate => candidate.Id == before.MonitorId);
            if (monitor is null || monitor.Dpi is 0)
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Unsupported, "The window monitor or its DPI is unavailable."));
                continue;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Cancelled, "Restore was cancelled before this item began mutation."));
                continue;
            }

            NativeRect? physicalTarget = null;
            if (item.TargetBounds is { } logicalTarget)
            {
                try
                {
                    physicalTarget = WindowsCoordinateConverter.ToPhysical(logicalTarget, monitor.Bounds, monitor.Dpi);
                    if (physicalTarget.Value.Width <= 0 || physicalTarget.Value.Height <= 0)
                    {
                        outcomes.Add(Outcome(item, WindowOutcomeCode.Unsupported,
                            "Target bounds must remain positive when represented by the native window API."));
                        continue;
                    }
                }
                catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
                {
                    outcomes.Add(Outcome(item, WindowOutcomeCode.Unsupported,
                        "Target bounds cannot be represented safely by the native window API."));
                    continue;
                }
            }

            nint handle = captured.Handle;
            bool restoredToNormalForBounds = false;
            WindowOutcome? mutationFailure = null;
            List<string> completedMutations = [];
            if (physicalTarget is { } target)
            {
                if (before.State is not NativeWindowState.Normal)
                {
                    NativeResult<bool> restored = native.ShowWindow(handle, NativeWindowState.Normal);
                    if (!restored.IsSuccess || restored.Value is not true)
                    {
                        mutationFailure = FromNativeError(item, restored.Error, restored.Message, "The application rejected restore before positioning.");
                    }
                    else
                    {
                        restoredToNormalForBounds = true;
                        completedMutations.Add("state changed to Normal before positioning");
                    }
                }
                if (mutationFailure is null)
                {
                    NativeResult<bool> moved = native.SetWindowPosition(handle, target);
                    if (!moved.IsSuccess || moved.Value is not true)
                        mutationFailure = FromNativeError(item, moved.Error, moved.Message, "The application rejected the requested move.");
                    else
                        completedMutations.Add("bounds changed");
                }
            }
            NativeWindowState? stateToApply = item.TargetState is { } targetState
                ? ToNativeState(targetState)
                : restoredToNormalForBounds ? before.State : null;
            if (mutationFailure is null && stateToApply is { } nativeTargetState &&
                !(restoredToNormalForBounds && nativeTargetState is NativeWindowState.Normal))
            {
                NativeResult<bool> shown = native.ShowWindow(handle, nativeTargetState);
                if (!shown.IsSuccess || shown.Value is not true)
                    mutationFailure = FromNativeError(item, shown.Error, shown.Message, "The application rejected the requested state.");
                else
                    completedMutations.Add($"state changed to {nativeTargetState}");
            }
            if (mutationFailure is { } failure)
            {
                outcomes.Add(completedMutations.Count == 0 ? failure : failure with
                {
                    Message = $"Partial apply: {string.Join("; ", completedMutations)}. Later mutation failed: {failure.Message}",
                });
                continue;
            }

            NativeResult<NativeWindowInfo> afterResult = default;
            bool verified = false;
            for (int attempt = 0; attempt < VerificationAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    try { await Task.Delay(VerificationDelay, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* Item mutation already began: skip waiting, finish bounded verification. */ }
                }
                afterResult = native.ObserveWindow(handle);
                if (!afterResult.IsSuccess || afterResult.Value is not { } candidate)
                    break;
                bool geometryMatches = item.TargetBounds is not { } expected ||
                    Close(WindowsCoordinateConverter.ToLogical(candidate.NormalBounds, monitor.Bounds, monitor.Dpi), expected);
                WindowState? expectedState = item.TargetState ?? (restoredToNormalForBounds ? ToState(before.State) : null);
                bool stateMatches = expectedState is not { } state || ToState(candidate.State) == state;
                if (geometryMatches && stateMatches)
                {
                    verified = true;
                    break;
                }
            }
            if (!afterResult.IsSuccess || afterResult.Value is not { } after)
            {
                outcomes.Add(FromNativeError(item, afterResult.Error, afterResult.Message, "Window vanished during verification."));
                continue;
            }
            outcomes.Add(mutationFailure ?? (verified
                ? Outcome(item, WindowOutcomeCode.Succeeded, "Applied and verified by re-observation.")
                : Outcome(item, WindowOutcomeCode.Failed, "The observed placement did not match the approved target within tolerance.")));
        }
        return outcomes.ToImmutable();
    }

    public Task<WindowSystemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(native.IsSupported
            ? new WindowSystemCapabilities(true, true, true, false,
                "Capture, state changes, and topology-planned bounds apply support valid attached monitors; application launch is not implemented.")
            : new WindowSystemCapabilities(false, false, false, false, "Win32 is available only on Windows."));
    }

    private static bool IsOrdinaryUserWindow(NativeWindowInfo info) =>
        info.IsVisible && !info.IsCloaked && !info.IsToolWindow && info.Owner == 0 && info.ProcessId != 0 &&
        !string.IsNullOrWhiteSpace(info.ApplicationId) && info.NormalBounds.Width > 0 && info.NormalBounds.Height > 0;

    private static bool HasValidActionTargets(RestorePlanItem item, out string message)
    {
        if (item.Action is RestoreAction.MoveResize && item.TargetBounds is not { } bounds)
        {
            message = "MoveResize requires target bounds.";
            return false;
        }
        if (item.Action is RestoreAction.ChangeState && (item.TargetState is null || item.TargetBounds is not null))
        {
            message = "ChangeState requires a target state and must not carry target bounds.";
            return false;
        }
        if (item.TargetBounds is { } target &&
            (!double.IsFinite(target.X) || !double.IsFinite(target.Y) ||
             !double.IsFinite(target.Width) || !double.IsFinite(target.Height) ||
             target.Width <= 0 || target.Height <= 0))
        {
            message = "Target bounds must contain finite coordinates and finite, positive dimensions.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool IsValidMonitor(NativeMonitorInfo monitor) =>
        monitor.Dpi is >= 48 and <= 960 && monitor.Bounds.Width > 0 && monitor.Bounds.Height > 0 &&
        monitor.WorkArea.Width > 0 && monitor.WorkArea.Height > 0;

    private static DisplaySnapshot ToDisplay(NativeMonitorInfo monitor) =>
        new($"monitor-{monitor.Id}", monitor.Name, WindowsCoordinateConverter.ToLogical(monitor.Bounds, monitor.Bounds, monitor.Dpi),
            WindowsCoordinateConverter.ToLogical(monitor.WorkArea, monitor.Bounds, monitor.Dpi), monitor.Dpi / 96d,
            monitor.Bounds.Height > monitor.Bounds.Width ? DisplayOrientation.Portrait : DisplayOrientation.Landscape,
            monitor.IsPrimary);

    private NativeMonitorInfo[] GetValidMonitors()
    {
        NativeResult<IReadOnlyList<NativeMonitorInfo>> result = native.EnumerateMonitors();
        return result.IsSuccess && result.Value is not null
            ? result.Value.Where(IsValidMonitor).GroupBy(monitor => monitor.Id).Select(group => group.First()).ToArray()
            : [];
    }

    private static NativeMonitorInfo? SelectTargetMonitor(NativeMonitorInfo[] monitors, DesktopRect target)
    {
        double centerX = target.X + target.Width / 2;
        double centerY = target.Y + target.Height / 2;
        return monitors
            .Select(monitor => (Monitor: monitor, Logical: ToDisplay(monitor).WorkArea))
            .OrderBy(candidate => Contains(candidate.Logical, centerX, centerY) ? 0 : 1)
            .ThenBy(candidate => DistanceSquared(candidate.Logical, centerX, centerY))
            .ThenBy(candidate => candidate.Monitor.Id)
            .Select(candidate => candidate.Monitor)
            .FirstOrDefault();
    }

    private static bool Contains(DesktopRect rectangle, double x, double y) =>
        x >= rectangle.X && x < rectangle.X + rectangle.Width && y >= rectangle.Y && y < rectangle.Y + rectangle.Height;

    private static double DistanceSquared(DesktopRect rectangle, double x, double y)
    {
        double nearestX = Math.Clamp(x, rectangle.X, rectangle.X + rectangle.Width);
        double nearestY = Math.Clamp(y, rectangle.Y, rectangle.Y + rectangle.Height);
        return Math.Pow(x - nearestX, 2) + Math.Pow(y - nearestY, 2);
    }

    private static NativeWindowState ToNativeState(WindowState state) => state switch
    {
        WindowState.Minimized => NativeWindowState.Minimized,
        WindowState.Maximized => NativeWindowState.Maximized,
        WindowState.Normal => NativeWindowState.Normal,
        _ => NativeWindowState.Normal,
    };

    private static bool Close(DesktopRect actual, DesktopRect expected) =>
        Math.Abs(actual.X - expected.X) <= 2 && Math.Abs(actual.Y - expected.Y) <= 2 &&
        Math.Abs(actual.Width - expected.Width) <= 2 && Math.Abs(actual.Height - expected.Height) <= 2;

    private static WindowOutcome Outcome(RestorePlanItem item, WindowOutcomeCode code, string message) =>
        new(item.SavedWindowId, item.CurrentWindowId, code, message);

    private static WindowOutcome FromNativeError(RestorePlanItem item, NativeError error, string detail, string fallback) =>
        Outcome(item, error switch
        {
            NativeError.AccessDenied => WindowOutcomeCode.PermissionDenied,
            NativeError.NotFound => WindowOutcomeCode.NotFound,
            NativeError.Unsupported or NativeError.InvalidData => WindowOutcomeCode.Unsupported,
            _ => WindowOutcomeCode.Failed,
        }, string.IsNullOrWhiteSpace(detail) ? fallback : detail);

    private static WindowState ToState(NativeWindowState state) => state switch
    {
        NativeWindowState.Minimized => WindowState.Minimized,
        NativeWindowState.Maximized => WindowState.Maximized,
        _ => WindowState.Normal,
    };

    private void EnsureSupported()
    {
        if (!native.IsSupported)
            throw new PlatformNotSupportedException("Win32 window integration is available only on Windows.");
    }

    private static T Require<T>(NativeResult<T> result, string operation) where T : class =>
        result.IsSuccess && result.Value is not null
            ? result.Value
            : throw new InvalidOperationException($"Native {operation} failed ({result.Error}): {result.Message}");

    private sealed record CapturedWindow(nint Handle, uint ProcessId, string ApplicationId, string? ExecutablePath);
}
