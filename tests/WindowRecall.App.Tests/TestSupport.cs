using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.App.Tests;

/// <summary>In-memory profile store for view-model tests (no filesystem races).</summary>
internal sealed class InMemoryProfileStore : IProfileStore
{
    private readonly Dictionary<string, LayoutProfile> store = new(StringComparer.Ordinal);

    public Dictionary<string, string> Serialized { get; } = new(StringComparer.Ordinal);

    public Task SaveAsync(string profileId, LayoutProfile profile, CancellationToken cancellationToken = default)
    {
        string json = ProfileJsonSerializer.Serialize(profile);
        store[profileId] = ProfileJsonSerializer.Deserialize(json);
        Serialized[profileId] = json;
        return Task.CompletedTask;
    }

    public Task<LayoutProfile> LoadAsync(string profileId, CancellationToken cancellationToken = default) =>
        store.TryGetValue(profileId, out LayoutProfile? profile)
            ? Task.FromResult(profile)
            : Task.FromException<LayoutProfile>(new FileNotFoundException(profileId));

    public Task<ImmutableArray<string>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(store.Keys.Order(StringComparer.Ordinal).ToImmutableArray());

    public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        store.Remove(profileId);
        Serialized.Remove(profileId);
        return Task.CompletedTask;
    }
}

/// <summary>Fake desktop with scripted behavior: capture results, per-item delays, and overrides so
/// cancellation and partial outcomes are deterministic. This is NOT proof that a real window moved.</summary>
internal sealed class FakeWindowSystem : IWindowSystem
{
    private CurrentDesktop desktop;
    private readonly WindowSystemCapabilities capabilities;

    public FakeWindowSystem(CurrentDesktop desktop, WindowSystemCapabilities? capabilities = null)
    {
        this.desktop = desktop;
        this.capabilities = capabilities ?? new WindowSystemCapabilities(true, true, true, false, null);
    }

    public int CaptureCalls { get; private set; }

    public int ApplyCalls { get; private set; }

    public Func<RestorePlanItem, WindowOutcome>? OutcomeOverride { get; set; }

    public Func<RestorePlanItem, CancellationToken, Task>? DelayPerItem { get; set; }

    public CancellationToken? LastApplyToken { get; private set; }

    public List<DesktopRect> AppliedBounds { get; } = new();

    public void SetDesktop(CurrentDesktop desktop) => this.desktop = desktop;

    public Task<CurrentDesktop> CaptureAsync(CancellationToken cancellationToken = default)
    {
        CaptureCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(desktop);
    }

    public async Task<ImmutableArray<WindowOutcome>> ApplyAsync(
        RestorePlan plan,
        ImmutableArray<string> approvedSavedWindowIds,
        CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        LastApplyToken = cancellationToken;
        RestorePlanItem item = plan.Items.Single();
        if (DelayPerItem is not null)
            await DelayPerItem(item, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (OutcomeOverride is not null)
            return [OutcomeOverride(item)];
        if (item.TargetBounds is not null)
            AppliedBounds.Add(item.TargetBounds);
        return [new WindowOutcome(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Succeeded, "Fake applied (no real window moved).")];
    }

    public Task<WindowSystemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(capabilities);
    }
}

/// <summary>Deterministic desktop fixtures for App workflow tests.</summary>
internal static class AppFixture
{
    public static readonly DisplaySnapshot Display1 = new(
        "display-1", "Built-in", new DesktopRect(0, 0, 1920, 1080), new DesktopRect(0, 0, 1920, 1040), 1, DisplayOrientation.Landscape, true);

    public static readonly DisplaySnapshot Display2 = new(
        "display-2", "Dock monitor", new DesktopRect(1920, 0, 2560, 1440), new DesktopRect(1920, 0, 2560, 1400), 1.5, DisplayOrientation.Landscape, false);

    public static WindowSnapshot Editor(string windowId = "win-editor", string displayId = "display-1", DesktopRect? bounds = null) =>
        new(
            windowId,
            new ApplicationIdentity("com.example.editor", "/Applications/Editor.app/Contents/MacOS/Editor"),
            "main",
            "Editor — notes.txt",
            bounds ?? new DesktopRect(100, 100, 900, 700),
            WindowState.Normal,
            displayId);

    public static WindowSnapshot Browser(string windowId = "win-browser", string title = "Browser") =>
        new(
            windowId,
            new ApplicationIdentity("com.example.browser"),
            "main",
            title,
            new DesktopRect(1000, 100, 800, 600),
            WindowState.Normal,
            "display-1");

    public static CurrentDesktop TwoWindowDesktop() =>
        new(
            new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            [Display1, Display2],
            [Editor(), Browser()]);

    public static LayoutProfile CapturedProfile(LaunchPolicy browserPolicy = LaunchPolicy.Never) =>
        new(
            LayoutProfile.CurrentSchemaVersion,
            "Desk",
            new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            [Display1, Display2],
            [
                Editor(bounds: new DesktopRect(0, 0, 1920, 1040)),
                Browser() with { LaunchPolicy = browserPolicy },
            ],
            new ProfilePrivacy(false));
}
