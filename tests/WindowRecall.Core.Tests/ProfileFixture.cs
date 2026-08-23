using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

internal static class ProfileFixture
{
    public static LayoutProfile Create(bool persistTitles = false, string? title = "Private document") => new(
        LayoutProfile.CurrentSchemaVersion,
        "Desk",
        new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        [new DisplaySnapshot("display-1", "Main", new DesktopRect(0, 0, 1920, 1080), new DesktopRect(0, 0, 1920, 1040), 1, DisplayOrientation.Landscape, true)],
        [new WindowSnapshot("window-1", new ApplicationIdentity("com.example.editor", "/Applications/Editor"), "editor", title, new DesktopRect(10, 20, 900, 700), WindowState.Normal, "display-1")],
        new ProfilePrivacy(persistTitles));
}
