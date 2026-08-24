using System.Collections.Immutable;
using System.Diagnostics;
using WindowRecall.Core;
using WindowRecall.Platform.Windows;
using Xunit;

namespace WindowRecall.Windows.Tests;

public sealed class WindowsDesktopIntegrationTests
{
    [WindowsDesktopFact]
    [Trait("Category", "WindowsDesktopIntegration")]
    public async Task Fixture_CaptureMoveRestore_ReobservesOriginalGeometry()
    {
        string fixture = Environment.GetEnvironmentVariable("WINDOW_RECALL_FIXTURE_EXE")
            ?? throw new InvalidOperationException("Set WINDOW_RECALL_FIXTURE_EXE to the built fixture executable.");
        string instanceId = Guid.NewGuid().ToString("N");
        ProcessStartInfo startInfo = new(fixture) { UseShellExecute = true };
        startInfo.ArgumentList.Add(instanceId);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the fixture.");
        try
        {
            WindowsWindowSystem system = new();
            ImmutableArray<WindowSnapshot> fixtureWindows = [];
            for (int attempt = 0; attempt < 100 && fixtureWindows.Length < 3; attempt++)
            {
                await Task.Delay(100);
                fixtureWindows = (await system.CaptureAsync()).Windows
                    .Where(window => window.Title?.EndsWith($" — {instanceId}", StringComparison.Ordinal) == true)
                    .ToImmutableArray();
            }
            Assert.Equal(3, fixtureWindows.Length);

            RestorePlan move = CreatePlan(fixtureWindows, window => window.NormalBounds with { X = window.NormalBounds.X + 30, Y = window.NormalBounds.Y + 30 });
            Assert.All(await system.ApplyAsync(move, move.Items.Select(item => item.SavedWindowId).ToImmutableArray()),
                outcome => Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code));

            RestorePlan restore = CreatePlan(fixtureWindows, window => window.NormalBounds);
            Assert.All(await system.ApplyAsync(restore, restore.Items.Select(item => item.SavedWindowId).ToImmutableArray()),
                outcome => Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static RestorePlan CreatePlan(ImmutableArray<WindowSnapshot> windows, Func<WindowSnapshot, DesktopRect> bounds) =>
        new(Guid.NewGuid(), windows.Select(window => new RestorePlanItem($"saved-{window.WindowId}", window.WindowId,
            RestoreAction.MoveResize, bounds(window), window.State, "fixture-approved")).ToImmutableArray(),
            new CurrentDesktop(DateTimeOffset.UtcNow, [], windows));
}

internal sealed class WindowsDesktopFactAttribute : FactAttribute
{
    public WindowsDesktopFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows.";
        else if (!Environment.UserInteractive)
            Skip = "Requires an interactive desktop.";
        else if (!string.Equals(Environment.GetEnvironmentVariable("WINDOW_RECALL_RUN_WINDOWS_INTEGRATION"), "1", StringComparison.Ordinal))
            Skip = "Opt in with WINDOW_RECALL_RUN_WINDOWS_INTEGRATION=1 and WINDOW_RECALL_FIXTURE_EXE.";
    }
}
