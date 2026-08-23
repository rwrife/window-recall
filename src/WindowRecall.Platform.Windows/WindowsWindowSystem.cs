using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Platform.Windows;

/// <summary>Milestone-one Windows seam. Native Win32 integration is intentionally deferred.</summary>
public sealed class WindowsWindowSystem : IWindowSystem
{
    public Task<CurrentDesktop> CaptureAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<CurrentDesktop>(new PlatformNotSupportedException("Live Windows window integration is not implemented yet."));

    public Task<ImmutableArray<WindowOutcome>> ApplyAsync(RestorePlan plan, ImmutableArray<string> approvedSavedWindowIds, CancellationToken cancellationToken = default) =>
        Task.FromResult(plan.Items.Select(item => new WindowOutcome(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Unsupported, "Live Windows window integration is not implemented yet.")).ToImmutableArray());

    public Task<WindowSystemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new WindowSystemCapabilities(false, false, false, false, "Win32 adapter is a compile-time seam only in milestone one."));
}
