using System.Collections.Immutable;

namespace WindowRecall.Core;

/// <summary>Persists versioned profiles in local storage.</summary>
public interface IProfileStore
{
    Task SaveAsync(string profileId, LayoutProfile profile, CancellationToken cancellationToken = default);
    Task<LayoutProfile> LoadAsync(string profileId, CancellationToken cancellationToken = default);
    Task<ImmutableArray<string>> ListAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string profileId, CancellationToken cancellationToken = default);
}

/// <summary>Platform adapter for observing and executing approved window operations.</summary>
public interface IWindowSystem
{
    Task<CurrentDesktop> CaptureAsync(CancellationToken cancellationToken = default);
    Task<ImmutableArray<WindowOutcome>> ApplyAsync(RestorePlan plan, ImmutableArray<string> approvedSavedWindowIds, CancellationToken cancellationToken = default);
    Task<WindowSystemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Describes adapter functionality without implying permission or integration success.</summary>
public sealed record WindowSystemCapabilities(bool CanObserve, bool CanMoveResize, bool CanChangeState, bool CanLaunch, string? Limitation);

/// <summary>Deterministically matches saved windows to current observations.</summary>
public interface IWindowMatcher
{
    Task<ImmutableArray<MatchCandidate>> MatchAsync(LayoutProfile profile, CurrentDesktop currentDesktop, CancellationToken cancellationToken = default);
}

/// <summary>Maps saved displays onto the currently attached topology.</summary>
public interface IDisplayMapper
{
    Task<ImmutableDictionary<string, string>> MapAsync(ImmutableArray<DisplaySnapshot> saved, ImmutableArray<DisplaySnapshot> current, CancellationToken cancellationToken = default);
}

/// <summary>Builds a safe preview; execution remains a separate, explicit operation.</summary>
public interface IRestorePlanner
{
    Task<RestorePlan> CreatePlanAsync(LayoutProfile profile, CurrentDesktop currentDesktop, CancellationToken cancellationToken = default);
}

/// <summary>Executes selected preview items and owns the single available undo step.</summary>
public interface IRestoreCoordinator
{
    Task<UndoReceipt> ApplyAsync(RestorePlan plan, CancellationToken cancellationToken = default);
    Task<ImmutableArray<WindowOutcome>> UndoAsync(UndoReceipt receipt, CancellationToken cancellationToken = default);
}
