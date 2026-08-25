using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Platform.MacOS;

internal interface IMacOSNativeApi
{
    bool GetAccessibilityTrust();
    ImmutableArray<MacOSDisplayObservation> ObserveDisplays();
    ImmutableArray<MacOSWindowObservation> ObserveWindows(bool includeAccessibilityDetails);
    MacOSMutationResult SetWindowBounds(MacOSWindowIdentity identity, DesktopRect bounds, CancellationToken cancellationToken);
    MacOSMutationResult SetWindowState(MacOSWindowIdentity identity, WindowState state, CancellationToken cancellationToken);
    MacOSWindowObservation? ReobserveWindow(uint windowId);
    bool OpenAccessibilitySettings();
}

internal sealed record MacOSWindowIdentity(
    uint WindowId,
    int OwnerPid,
    string BundleIdentifier,
    string ExecutableUrl);

internal sealed record MacOSDisplayObservation(
    uint DisplayId,
    string? Name,
    DesktopRect Bounds,
    DesktopRect WorkArea,
    double ScaleFactor,
    DisplayOrientation Orientation,
    bool IsPrimary);

internal sealed record MacOSWindowObservation(
    uint WindowId,
    int OwnerPid,
    string? BundleIdentifier,
    string? ExecutableUrl,
    DesktopRect Bounds,
    bool IsOnScreen,
    int Layer,
    bool IsSystemOwned,
    bool IsModal,
    bool IsHidden,
    bool IsFullScreen,
    bool IsMinimized,
    bool SupportsPosition,
    bool SupportsSize,
    WindowState State,
    uint? DisplayId);

internal enum MacOSNativeError
{
    None,
    PermissionDenied,
    StaleElement,
    Rejected,
    Unsupported,
    Cancelled,
    Failed,
}

internal sealed record MacOSMutationResult(MacOSNativeError Error, string? Detail = null)
{
    public static MacOSMutationResult Success { get; } = new(MacOSNativeError.None);
}
