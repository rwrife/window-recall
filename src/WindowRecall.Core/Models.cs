using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace WindowRecall.Core;

/// <summary>Identifies an operating-system application without exposing a native handle.</summary>
/// <param name="ApplicationId">A bundle identifier, package identity, or executable identity.</param>
/// <param name="ExecutablePath">An optional absolute executable path; never a shell command.</param>
public sealed record ApplicationIdentity(
    [property: JsonRequired] string ApplicationId,
    string? ExecutablePath = null);

/// <summary>A platform-neutral rectangle in logical desktop coordinates.</summary>
public sealed record DesktopRect(
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y,
    [property: JsonRequired] double Width,
    [property: JsonRequired] double Height);

/// <summary>A platform-neutral display orientation.</summary>
public enum DisplayOrientation { Landscape, Portrait, LandscapeFlipped, PortraitFlipped }

/// <summary>A captured display and the stable hints used to map it later.</summary>
public sealed record DisplaySnapshot(
    [property: JsonRequired] string Id,
    string? Name,
    [property: JsonRequired] DesktopRect Bounds,
    [property: JsonRequired] DesktopRect WorkArea,
    [property: JsonRequired] double ScaleFactor,
    [property: JsonRequired] DisplayOrientation Orientation,
    [property: JsonRequired] bool IsPrimary);

/// <summary>The ordinary placement state of a window.</summary>
public enum WindowState { Normal, Minimized, Maximized, FullScreen }

/// <summary>Controls whether a missing application may be launched after explicit approval.</summary>
public enum LaunchPolicy { Never, AllowExplicitLaunch }

/// <summary>A captured user-owned window. <see cref="WindowId"/> is an opaque session identifier, not a native handle.</summary>
public sealed record WindowSnapshot(
    [property: JsonRequired] string WindowId,
    [property: JsonRequired] ApplicationIdentity Application,
    string? Role,
    string? Title,
    [property: JsonRequired] DesktopRect NormalBounds,
    [property: JsonRequired] WindowState State,
    string? DisplayId,
    LaunchPolicy LaunchPolicy = LaunchPolicy.Never);

/// <summary>Privacy choices embedded in a profile.</summary>
public sealed record ProfilePrivacy(bool PersistWindowTitles = false);

/// <summary>A portable, versioned description of a desired desktop layout.</summary>
public sealed record LayoutProfile(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Name,
    [property: JsonRequired] DateTimeOffset CapturedAtUtc,
    [property: JsonRequired] ImmutableArray<DisplaySnapshot> Displays,
    [property: JsonRequired] ImmutableArray<WindowSnapshot> Windows,
    [property: JsonRequired] ProfilePrivacy Privacy)
{
    /// <summary>The only schema version supported by this build.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>The displays and windows observed at one instant.</summary>
public sealed record CurrentDesktop(
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<DisplaySnapshot> Displays,
    ImmutableArray<WindowSnapshot> Windows);

/// <summary>Evidence for a possible saved-to-current window match.</summary>
public sealed record MatchCandidate(string SavedWindowId, string CurrentWindowId, double Confidence, ImmutableArray<string> Evidence);

/// <summary>The action represented by a restore-plan item.</summary>
public enum RestoreAction { MoveResize, ChangeState, Launch, Skip, Ambiguous }

/// <summary>A single immutable preview item. Ambiguous items must never be automatically applied.</summary>
public sealed record RestorePlanItem(
    string SavedWindowId,
    string? CurrentWindowId,
    RestoreAction Action,
    DesktopRect? TargetBounds,
    WindowState? TargetState,
    string Reason)
{
    /// <summary>Only deterministic operations on an existing window may be selected automatically.</summary>
    public bool CanAutoApply =>
        CurrentWindowId is not null && (Action is RestoreAction.MoveResize or RestoreAction.ChangeState);
}

/// <summary>A preview that must be approved before execution and captures the desktop needed for undo.</summary>
public sealed record RestorePlan(Guid PlanId, ImmutableArray<RestorePlanItem> Items, CurrentDesktop UndoSnapshot);

/// <summary>A structured capability or execution status for one window.</summary>
public enum WindowOutcomeCode { Succeeded, Skipped, Cancelled, NotFound, Ambiguous, PermissionDenied, Unsupported, Failed }

/// <summary>The result of one attempted plan item.</summary>
public sealed record WindowOutcome(string SavedWindowId, string? CurrentWindowId, WindowOutcomeCode Code, string Message);

/// <summary>A receipt retaining undo state even when only part of a restore succeeded.</summary>
public sealed record UndoReceipt(
    Guid ReceiptId,
    Guid PlanId,
    DateTimeOffset AppliedAtUtc,
    CurrentDesktop BeforeRestore,
    ImmutableArray<WindowOutcome> Outcomes);
