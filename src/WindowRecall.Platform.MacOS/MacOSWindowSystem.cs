using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Platform.MacOS;

/// <summary>Milestone-one macOS seam. Native Accessibility/CoreGraphics integration is intentionally deferred.</summary>
public sealed class MacOSWindowSystem : IWindowSystem
{
    public Task<CurrentDesktop> CaptureAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<CurrentDesktop>(new PlatformNotSupportedException("Live macOS window integration is not implemented yet."));

    public Task<ImmutableArray<WindowOutcome>> ApplyAsync(RestorePlan plan, ImmutableArray<string> approvedSavedWindowIds, CancellationToken cancellationToken = default) =>
        Task.FromResult(plan.Items.Select(item => new WindowOutcome(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Unsupported, "Live macOS window integration is not implemented yet.")).ToImmutableArray());

    public Task<WindowSystemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new WindowSystemCapabilities(false, false, false, false, "Accessibility/CoreGraphics adapter is a compile-time seam only in milestone one."));
}
