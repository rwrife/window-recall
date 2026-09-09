using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Cli;

/// <summary>Fallback adapter for operating systems with no Window Recall desktop integration
/// (and for headless sessions). Reports no capability instead of pretending to observe windows.</summary>
public sealed class UnsupportedDesktopAdapter : IWindowSystem
{
    private readonly string limitation;

    public UnsupportedDesktopAdapter()
        : this("No Window Recall desktop integration is available on this platform or session. Capture and apply require an interactive Windows 10/11 or macOS desktop.")
    {
    }

    public UnsupportedDesktopAdapter(string limitation) =>
        this.limitation = limitation ?? throw new ArgumentNullException(nameof(limitation));

    public Task<CurrentDesktop> CaptureAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<CurrentDesktop>(new PlatformNotSupportedException(limitation));

    public Task<ImmutableArray<WindowOutcome>> ApplyAsync(RestorePlan plan, ImmutableArray<string> approvedSavedWindowIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.FromResult(plan.Items
            .Select(item => new WindowOutcome(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Unsupported, limitation))
            .ToImmutableArray());
    }

    public Task<WindowSystemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WindowSystemCapabilities(false, false, false, false, limitation));
    }
}

/// <summary>Opens the live desktop adapter for the current operating system through the Core
/// <see cref="IWindowSystem"/> boundary only; the CLI never calls native APIs directly.</summary>
public static class LiveDesktop
{
    public static IWindowSystem Open()
    {
        if (OperatingSystem.IsWindows())
            return new Platform.Windows.WindowsWindowSystem();
        if (OperatingSystem.IsMacOS())
            return new Platform.MacOS.MacOSWindowSystem();
        return new UnsupportedDesktopAdapter();
    }
}
