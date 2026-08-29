using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

public sealed class RestorePlannerTests
{
    [Fact]
    public async Task PreviewIsStableSafeAndPreservesWindowState()
    {
        DisplaySnapshot savedDisplay = Display("saved-display", 0, 0, 1920, 1040, true);
        DisplaySnapshot currentDisplay = Display("current-display", -1280, 0, 1280, 680, true);
        LayoutProfile profile = Profile([savedDisplay],
            Window("z", "app-z", new(960, 520, 1200, 900), WindowState.Maximized, "saved-display"),
            Window("a", "app-a", new(0, 0, 800, 600), WindowState.Minimized, "saved-display"));
        CurrentDesktop current = Desktop([currentDisplay],
            Window("current-z", "app-z", new(0, 0, 10, 10), WindowState.Normal, "current-display"),
            Window("current-a", "app-a", new(0, 0, 10, 10), WindowState.Normal, "current-display"));

        RestorePlan plan = await new RestorePlanner().CreatePlanAsync(profile, current);

        Assert.Equal(["a", "z"], plan.Items.Select(item => item.SavedWindowId));
        Assert.All(plan.Items, item =>
        {
            Assert.Equal(RestoreAction.MoveResize, item.Action);
            Assert.True(item.IsIncluded);
            Assert.True(item.CanAutoApply);
            Assert.NotNull(item.TargetBounds);
        });
        Assert.Equal(WindowState.Minimized, plan.Items[0].TargetState);
        Assert.Equal(WindowState.Maximized, plan.Items[1].TargetState);
        Assert.Same(current, plan.UndoSnapshot);
    }

    [Fact]
    public async Task AmbiguousSameAppWindowsAreExcludedAndNeverAutoMove()
    {
        DisplaySnapshot display = Display("display", 0, 0, 1000, 700, true);
        LayoutProfile profile = Profile([display], Window("saved", "app", new(0, 0, 100, 100), WindowState.Normal, "display"));
        CurrentDesktop current = Desktop([display],
            Window("b", "app", new(0, 0, 100, 100), WindowState.Normal, "display"),
            Window("a", "app", new(0, 0, 100, 100), WindowState.Normal, "display"));

        RestorePlanItem item = Assert.Single((await new RestorePlanner().CreatePlanAsync(profile, current)).Items);

        Assert.Equal(RestoreAction.Ambiguous, item.Action);
        Assert.Null(item.CurrentWindowId);
        Assert.False(item.IsIncluded);
        Assert.Contains("explicit", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(LaunchPolicy.Never, RestoreAction.Skip)]
    [InlineData(LaunchPolicy.AllowExplicitLaunch, RestoreAction.Launch)]
    public async Task MissingWindowsAreReasonedAndNeverSelectedByDefault(LaunchPolicy policy, RestoreAction action)
    {
        DisplaySnapshot display = Display("display", 0, 0, 1000, 700, true);
        WindowSnapshot missing = Window("missing", "app", new(0, 0, 100, 100), WindowState.Normal, "display") with { LaunchPolicy = policy };

        RestorePlan plan = await new RestorePlanner().CreatePlanAsync(Profile([display], missing), Desktop([display]));
        RestorePlanItem item = Assert.Single(plan.Items);

        Assert.Equal(action, item.Action);
        Assert.False(item.IsIncluded);
        Assert.NotEmpty(item.Reason);
    }

    [Fact]
    public async Task SelectionReturnsANewPlanAndLaunchNeedsSeparateOptIn()
    {
        DisplaySnapshot display = Display("display", 0, 0, 1000, 700, true);
        LayoutProfile profile = Profile([display],
            Window("matched", "matched-app", new(0, 0, 100, 100), WindowState.Normal, "display"),
            Window("launch", "missing-app", new(0, 0, 100, 100), WindowState.Normal, "display") with { LaunchPolicy = LaunchPolicy.AllowExplicitLaunch });
        RestorePlan original = await new RestorePlanner().CreatePlanAsync(profile,
            Desktop([display], Window("current", "matched-app", new(0, 0, 100, 100), WindowState.Normal, "display")));

        RestorePlan ordinary = original.WithSelection(["launch"], includeLaunches: false);
        RestorePlan explicitLaunch = original.WithSelection(["launch"], includeLaunches: true);

        Assert.True(Assert.Single(original.Items, item => item.SavedWindowId == "matched").IsIncluded);
        Assert.All(ordinary.Items, item => Assert.False(item.IsIncluded));
        Assert.True(Assert.Single(explicitLaunch.Items, item => item.SavedWindowId == "launch").IsIncluded);
        Assert.NotSame(original, ordinary);
    }

    [Fact]
    public async Task NoCurrentDisplaysProducesReasonedSkip()
    {
        DisplaySnapshot display = Display("display", 0, 0, 1000, 700, true);
        LayoutProfile profile = Profile([display], Window("saved", "app", new(0, 0, 100, 100), WindowState.Normal, "display"));
        CurrentDesktop current = Desktop([], Window("current", "app", new(0, 0, 100, 100), WindowState.Normal, null));

        RestorePlanItem item = Assert.Single((await new RestorePlanner().CreatePlanAsync(profile, current)).Items);

        Assert.Equal(RestoreAction.Skip, item.Action);
        Assert.Contains("display", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static LayoutProfile Profile(ImmutableArray<DisplaySnapshot> displays, params WindowSnapshot[] windows) =>
        new(1, "profile", DateTimeOffset.UnixEpoch, displays, windows.ToImmutableArray(), new(false));

    private static CurrentDesktop Desktop(ImmutableArray<DisplaySnapshot> displays, params WindowSnapshot[] windows) =>
        new(DateTimeOffset.UnixEpoch, displays, windows.ToImmutableArray());

    private static DisplaySnapshot Display(string id, double x, double y, double width, double height, bool primary) =>
        new(id, id, new(x, y, width, height + 40), new(x, y, width, height), 1, DisplayOrientation.Landscape, primary);

    private static WindowSnapshot Window(string id, string app, DesktopRect bounds, WindowState state, string? display) =>
        new(id, new(app), null, null, bounds, state, display);
}
