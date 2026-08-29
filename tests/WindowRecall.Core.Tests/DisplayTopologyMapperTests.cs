using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

public sealed class DisplayTopologyMapperTests
{
    [Fact]
    public async Task StableHintDominatesChangedScaleSizeAndPosition()
    {
        DisplaySnapshot saved = Display("stable", "Desk", -1920, 0, 1920, 1040, 1, false);
        DisplaySnapshot misleading = Display("other", "Other", -1920, 0, 1920, 1040, 1, false);
        DisplaySnapshot stable = Display("stable", "Desk", 0, 0, 1280, 680, 1.5, true);

        DisplayMapping mapping = Assert.Single(await new DisplayTopologyMapper().MapDetailedAsync([saved], [misleading, stable]));

        Assert.Equal("stable", mapping.CurrentDisplayId);
        Assert.Equal(DisplayMappingKind.StableHint, mapping.Kind);
    }

    [Fact]
    public async Task RelativeTopologyMapsRenamedNegativeCoordinateDisplays()
    {
        DisplaySnapshot[] saved =
        [
            Display("saved-left", "old-left", -1200, 0, 1200, 1600, 1, false, DisplayOrientation.Portrait),
            Display("saved-main", "old-main", 0, 0, 1920, 1040, 1, true),
        ];
        DisplaySnapshot[] current =
        [
            Display("current-main", "new-main", 0, 0, 2560, 1360, 1.5, true),
            Display("current-left", "new-left", -1080, 100, 1080, 1880, 2, false, DisplayOrientation.Portrait),
        ];

        ImmutableDictionary<string, string> result = await new DisplayTopologyMapper().MapAsync([.. saved], [.. current]);

        Assert.Equal("current-left", result["saved-left"]);
        Assert.Equal("current-main", result["saved-main"]);
    }

    [Fact]
    public async Task MissingDisplayFallsBackToCurrentPrimaryWithReason()
    {
        DisplaySnapshot[] saved =
        [
            Display("main", "main", 0, 0, 1000, 700, 1, true),
            Display("gone", "projector", 1000, 0, 1920, 1080, 1, false),
        ];
        DisplaySnapshot current = Display("laptop", "laptop", 0, 0, 1440, 860, 2, true);

        ImmutableArray<DisplayMapping> mappings = await new DisplayTopologyMapper().MapDetailedAsync([.. saved], [current]);

        DisplayMapping missing = Assert.Single(mappings, mapping => mapping.SavedDisplayId == "gone");
        Assert.Equal("laptop", missing.CurrentDisplayId);
        Assert.Equal(DisplayMappingKind.PrimaryFallback, missing.Kind);
        Assert.Contains("missing", missing.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizedConversionHandlesRotationScaleAndReducedWorkArea()
    {
        DisplaySnapshot saved = Display("saved", null, -1920, -100, 1920, 1040, 1, true);
        DisplaySnapshot current = Display("current", null, 100, -1200, 900, 1500, 2, true, DisplayOrientation.Portrait);
        DesktopRect bounds = new(-1440, 160, 960, 520);

        DesktopRect converted = RestoreGeometry.ConvertNormalized(bounds, saved.WorkArea, current.WorkArea);

        Assert.Equal(new DesktopRect(325, -825, 450, 750), converted);
    }

    [Theory]
    [InlineData(-100000, -100000, 200, 100)]
    [InlineData(100000, 100000, 5000, 5000)]
    [InlineData(-500, 200, 1, 1)]
    public void ClampAlwaysLeavesARecoverableRegionOnScreen(double x, double y, double width, double height)
    {
        DesktopRect workArea = new(-1280, 20, 1280, 680);

        DesktopRect result = RestoreGeometry.ClampRecoverablyOnScreen(new(x, y, width, height), workArea);

        Assert.True(result.Width > 0 && result.Height > 0);
        Assert.True(result.X + result.Width >= workArea.X + RestoreGeometry.MinimumVisibleWidth);
        Assert.True(result.X <= workArea.X + workArea.Width - RestoreGeometry.MinimumVisibleWidth);
        Assert.True(result.Y >= workArea.Y);
        Assert.True(result.Y <= workArea.Y + workArea.Height - RestoreGeometry.MinimumVisibleHeight);
    }

    [Fact]
    public void SeededGeometryPropertyIsFiniteBoundedAndDeterministic()
    {
        Random random = new(424242);
        DesktopRect workArea = new(-1600, -900, 1600, 860);
        for (int index = 0; index < 1000; index++)
        {
            DesktopRect input = new(random.Next(-10000, 10000), random.Next(-10000, 10000),
                random.Next(1, 10000), random.Next(1, 10000));
            DesktopRect first = RestoreGeometry.ClampRecoverablyOnScreen(input, workArea);
            DesktopRect second = RestoreGeometry.ClampRecoverablyOnScreen(input, workArea);
            Assert.Equal(first, second);
            Assert.All(new[] { first.X, first.Y, first.Width, first.Height }, value => Assert.True(double.IsFinite(value)));
            Assert.InRange(first.Width, 1, workArea.Width);
            Assert.InRange(first.Height, 1, workArea.Height);
        }
    }

    private static DisplaySnapshot Display(string id, string? name, double x, double y, double width, double height,
        double scale, bool primary, DisplayOrientation orientation = DisplayOrientation.Landscape) =>
        new(id, name, new(x, y, width, height + 40), new(x, y, width, height), scale, orientation, primary);
}
