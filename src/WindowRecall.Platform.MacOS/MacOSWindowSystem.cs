using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Platform.MacOS;

/// <summary>Capability-aware CoreGraphics and Accessibility window-system adapter.</summary>
public sealed class MacOSWindowSystem : IWindowSystem
{
    private const int VerificationAttempts = 3;
    private static readonly TimeSpan VerificationDelay = TimeSpan.FromMilliseconds(20);
    private readonly IMacOSNativeApi native;
    private ImmutableDictionary<string, CapturedWindow> capturedWindows =
        ImmutableDictionary<string, CapturedWindow>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>Creates the live adapter. Native calls execute only on macOS.</summary>
    public MacOSWindowSystem()
        : this(new MacOSNativeApi())
    {
    }

    internal MacOSWindowSystem(IMacOSNativeApi native) =>
        this.native = native ?? throw new ArgumentNullException(nameof(native));

    /// <inheritdoc />
    public Task<WindowSystemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (native is MacOSNativeApi && !OperatingSystem.IsMacOS())
        {
            return Task.FromResult(new WindowSystemCapabilities(false, false, false, false, "The macOS adapter can execute only on macOS."));
        }

        bool trusted = native.GetAccessibilityTrust();
        return Task.FromResult(new WindowSystemCapabilities(
            true,
            trusted,
            trusted,
            false,
            trusted ? null : "Accessibility permission is absent. Capture uses read-only CoreGraphics observations; moving, resizing, and state inspection are unavailable."));
    }

    /// <inheritdoc />
    public Task<CurrentDesktop> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool trusted = native.GetAccessibilityTrust();
        ImmutableArray<MacOSDisplayObservation> observedDisplays = native.ObserveDisplays();
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<MacOSWindowObservation> observedWindows = native.ObserveWindows(trusted);
        cancellationToken.ThrowIfCancellationRequested();

        ImmutableDictionary<string, CapturedWindow>.Builder nextSession =
            ImmutableDictionary.CreateBuilder<string, CapturedWindow>(StringComparer.Ordinal);
        ImmutableArray<WindowSnapshot>.Builder windows = ImmutableArray.CreateBuilder<WindowSnapshot>();
        foreach (MacOSWindowObservation window in observedWindows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsObservableApplicationWindow(window))
            {
                continue;
            }

            string sessionId = $"window-{Guid.NewGuid():N}";
            nextSession.Add(sessionId, new CapturedWindow(new MacOSWindowIdentity(
                window.WindowId,
                window.OwnerPid,
                window.BundleIdentifier!,
                window.ExecutableUrl!)));
            windows.Add(ToSnapshot(window, sessionId, GetRestoreLimitation(window, trusted)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<DisplaySnapshot> displays = observedDisplays.Select(ToSnapshot).ToImmutableArray();
        Interlocked.Exchange(ref capturedWindows, nextSession.ToImmutable());
        return Task.FromResult(new CurrentDesktop(DateTimeOffset.UtcNow, displays, windows.ToImmutable()));
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<WindowOutcome>> ApplyAsync(
        RestorePlan plan,
        ImmutableArray<string> approvedSavedWindowIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        HashSet<string> duplicateApprovals = approvedSavedWindowIds
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> approved = approvedSavedWindowIds.ToHashSet(StringComparer.Ordinal);
        HashSet<string> duplicatePlanIds = plan.Items
            .GroupBy(item => item.SavedWindowId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> duplicateCurrentWindowIds = plan.Items
            .Where(item => item.CurrentWindowId is not null)
            .GroupBy(item => item.CurrentWindowId!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
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
            if (item.CurrentWindowId is not null && duplicateCurrentWindowIds.Contains(item.CurrentWindowId))
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Failed,
                    "The restore plan targets the same current window more than once."));
                continue;
            }
            if (!approved.Contains(item.SavedWindowId))
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Skipped, "Window was not approved."));
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
            if (item.CurrentWindowId is null || !session.TryGetValue(item.CurrentWindowId, out CapturedWindow? captured))
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.NotFound, "The captured window session ID is missing or stale."));
                continue;
            }
            if (!native.GetAccessibilityTrust())
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.PermissionDenied, "Accessibility permission is required to change windows."));
                continue;
            }

            MacOSWindowObservation? before = native.ReobserveWindow(captured.Identity.WindowId);
            if (before is null)
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.NotFound, "The Accessibility window element is stale."));
                continue;
            }
            if (!SameIdentity(before, captured))
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.NotFound,
                    "The native window ID no longer belongs to the application captured for this session."));
                continue;
            }
            if (GetRestoreLimitation(before, accessibilityTrusted: true) is { } limitation)
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Skipped, limitation));
                continue;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(Outcome(item, WindowOutcomeCode.Cancelled, "Restore was cancelled before this item began mutation."));
                continue;
            }

            MacOSMutationResult result = ApplyMutations(item, captured.Identity, cancellationToken);
            if (result.Error != MacOSNativeError.None)
            {
                outcomes.Add(MapError(item, result));
                continue;
            }

            MacOSWindowObservation? after = null;
            bool observed = false;
            for (int attempt = 0; attempt < VerificationAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    try { await Task.Delay(VerificationDelay, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* Mutation began; finish bounded verification without waiting. */ }
                }

                after = native.ReobserveWindow(captured.Identity.WindowId);
                if (after is null || !SameIdentity(after, captured))
                {
                    break;
                }
                observed = item.Action switch
                {
                    RestoreAction.MoveResize => Close(after.Bounds, item.TargetBounds!) &&
                        (item.TargetState is null || after.State == item.TargetState),
                    RestoreAction.ChangeState => after.State == item.TargetState,
                    _ => false,
                };
                if (observed)
                {
                    break;
                }
            }

            outcomes.Add(after is null || !SameIdentity(after, captured)
                ? Outcome(item, WindowOutcomeCode.NotFound, "The window became stale during verification.")
                : observed
                    ? Outcome(item, WindowOutcomeCode.Succeeded, "The requested change was observed within tolerance.")
                    : Outcome(item, WindowOutcomeCode.Failed, "The application did not retain the requested change within tolerance."));
        }

        return outcomes.ToImmutable();
    }

    private static bool HasValidActionTargets(RestorePlanItem item, out string message)
    {
        if (item.Action is RestoreAction.MoveResize &&
            (item.TargetBounds is null || item.TargetState is not (null or WindowState.Normal or WindowState.Minimized)))
        {
            message = "MoveResize requires target bounds and may carry only a supported target state.";
            return false;
        }
        if (item.Action is RestoreAction.ChangeState &&
            (item.TargetBounds is not null || item.TargetState is not (WindowState.Normal or WindowState.Minimized)))
        {
            message = "ChangeState requires a supported target state and must not carry target bounds.";
            return false;
        }
        if (item.TargetBounds is { } bounds &&
            (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
             !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
             bounds.Width <= 0 || bounds.Height <= 0))
        {
            message = "Target bounds must contain finite coordinates and finite, positive dimensions.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool SameIdentity(MacOSWindowObservation observed, CapturedWindow captured) =>
        SameIdentity(observed, captured.Identity);

    private MacOSMutationResult ApplyMutations(
        RestorePlanItem item,
        MacOSWindowIdentity identity,
        CancellationToken cancellationToken)
    {
        if (item.Action is RestoreAction.ChangeState)
        {
            return native.SetWindowState(identity, item.TargetState!.Value, cancellationToken);
        }

        MacOSMutationResult boundsResult = native.SetWindowBounds(identity, item.TargetBounds!, cancellationToken);
        if (boundsResult.Error is not MacOSNativeError.None || item.TargetState is null)
        {
            return boundsResult;
        }

        // Bounds mutation has started, so finish this item even if cancellation arrives before state mutation.
        MacOSMutationResult stateResult = native.SetWindowState(identity, item.TargetState.Value, CancellationToken.None);
        return stateResult.Error is MacOSNativeError.None
            ? stateResult
            : stateResult with
            {
                Detail = $"Partial apply: bounds changed; state change failed. {stateResult.Detail ?? stateResult.Error.ToString()}",
            };
    }

    private static bool SameIdentity(MacOSWindowObservation observed, MacOSWindowIdentity captured) =>
        observed.WindowId == captured.WindowId &&
        observed.OwnerPid == captured.OwnerPid &&
        StringComparer.Ordinal.Equals(observed.BundleIdentifier, captured.BundleIdentifier) &&
        StringComparer.Ordinal.Equals(observed.ExecutableUrl, captured.ExecutableUrl);

    private static bool Close(DesktopRect actual, DesktopRect expected) =>
        Math.Abs(actual.X - expected.X) <= 2 && Math.Abs(actual.Y - expected.Y) <= 2 &&
        Math.Abs(actual.Width - expected.Width) <= 2 && Math.Abs(actual.Height - expected.Height) <= 2;

    private static bool IsObservableApplicationWindow(MacOSWindowObservation window) =>
        window.OwnerPid > 0 &&
        window.Layer == 0 &&
        double.IsFinite(window.Bounds.X) &&
        double.IsFinite(window.Bounds.Y) &&
        double.IsFinite(window.Bounds.Width) &&
        double.IsFinite(window.Bounds.Height) &&
        window.Bounds.Width > 0 &&
        window.Bounds.Height > 0 &&
        !string.IsNullOrWhiteSpace(window.BundleIdentifier) &&
        window.ExecutableUrl is not null;

    private static string? GetRestoreLimitation(MacOSWindowObservation window, bool accessibilityTrusted)
    {
        if (window.IsSystemOwned) return "Restore skipped: the window is system-owned.";
        if (window.IsModal) return "Restore skipped: the window is modal.";
        if (window.IsHidden || (!window.IsOnScreen && !window.IsMinimized))
            return "Restore skipped: the window is hidden or off-screen.";
        if (window.IsFullScreen || window.State is WindowState.FullScreen)
            return "Restore skipped: full-screen windows are not changed automatically.";
        if (accessibilityTrusted && (!window.SupportsPosition || !window.SupportsSize))
            return "Restore skipped: the window does not expose settable Accessibility position and size attributes.";
        return null;
    }

    private static DisplaySnapshot ToSnapshot(MacOSDisplayObservation display) =>
        new($"cg:{display.DisplayId}", display.Name, display.Bounds, display.WorkArea, display.ScaleFactor, display.Orientation, display.IsPrimary);

    private static WindowSnapshot ToSnapshot(MacOSWindowObservation window, string sessionId, string? restoreLimitation) =>
        new(
            sessionId,
            new ApplicationIdentity(window.BundleIdentifier!, ToExecutablePath(window.ExecutableUrl!)),
            restoreLimitation is null ? "AXWindow" : $"AXWindow; restore-skip={restoreLimitation}",
            null,
            window.Bounds,
            window.IsMinimized ? WindowState.Minimized : window.State,
            window.DisplayId is uint displayId ? $"cg:{displayId}" : null);

    private static string? ToExecutablePath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && parsed.IsFile ? parsed.LocalPath : null;

    private static WindowOutcome MapError(RestorePlanItem item, MacOSMutationResult result) =>
        Outcome(item, result.Error switch
        {
            MacOSNativeError.PermissionDenied => WindowOutcomeCode.PermissionDenied,
            MacOSNativeError.StaleElement => WindowOutcomeCode.NotFound,
            MacOSNativeError.Unsupported => WindowOutcomeCode.Unsupported,
            MacOSNativeError.Cancelled => WindowOutcomeCode.Cancelled,
            _ => WindowOutcomeCode.Failed,
        }, string.IsNullOrWhiteSpace(result.Detail) ? result.Error switch
        {
            MacOSNativeError.PermissionDenied => "Accessibility permission was denied.",
            MacOSNativeError.StaleElement => "The Accessibility window element is stale.",
            MacOSNativeError.Rejected => "The application rejected the requested operation.",
            MacOSNativeError.Unsupported => "The window does not support the requested operation.",
            MacOSNativeError.Cancelled => "Restore was cancelled immediately before native mutation.",
            _ => "The native window operation failed.",
        } : result.Detail);

    private static WindowOutcome Outcome(RestorePlanItem item, WindowOutcomeCode code, string message) =>
        new(item.SavedWindowId, item.CurrentWindowId, code, message);

    private sealed record CapturedWindow(MacOSWindowIdentity Identity);
}
