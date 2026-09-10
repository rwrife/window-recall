using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using WindowRecall.Core;
using WindowRecall.Platform.MacOS;
using Xunit;

namespace WindowRecall.MacOS.Tests;

public sealed class MacOSDesktopIntegrationTests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    [MacOSDesktopFact]
    [Trait("Category", "MacOSDesktopIntegration")]
    public async Task Fixture_CaptureMoveRestore_RecordsRedactedMachineReadableEvidence()
    {
        string fixture = RequiredEnvironment("WINDOW_RECALL_MACOS_FIXTURE");
        string output = RequiredEnvironment("WINDOW_RECALL_MACOS_EVIDENCE");
        using Process process = Process.Start(new ProcessStartInfo(fixture) { UseShellExecute = true })
            ?? throw new InvalidOperationException("Could not start the macOS fixture.");
        try
        {
            MacOSWindowSystem system = new();
            ImmutableArray<WindowSnapshot> windows = [];
            for (int attempt = 0; attempt < 100 && windows.Length < 3; attempt++)
            {
                await Task.Delay(100);
                windows = (await system.CaptureAsync()).Windows
                    .Where(window => window.Application.ApplicationId == "org.windowrecall.macosfixture")
                    .OrderBy(window => window.NormalBounds.X)
                    .ThenBy(window => window.NormalBounds.Y)
                    .ToImmutableArray();
            }

            Assert.Equal(3, windows.Length);
            RestorePlan move = Plan(windows, window => window.NormalBounds with { X = window.NormalBounds.X + 30, Y = window.NormalBounds.Y + 30 });
            ImmutableArray<WindowOutcome> moved = await system.ApplyAsync(move, Approvals(move));
            ImmutableArray<WindowSnapshot> afterMove = (await system.CaptureAsync()).Windows
                .Where(window => window.Application.ApplicationId == "org.windowrecall.macosfixture")
                .OrderBy(window => window.NormalBounds.X)
                .ThenBy(window => window.NormalBounds.Y)
                .ToImmutableArray();
            Assert.Equal(windows.Length, afterMove.Length);
            Dictionary<string, DesktopRect> originalByCurrentId = afterMove
                .Select((window, index) => (window.WindowId, windows[index].NormalBounds))
                .ToDictionary(pair => pair.WindowId, pair => pair.NormalBounds, StringComparer.Ordinal);
            RestorePlan restore = Plan(afterMove, window => originalByCurrentId[window.WindowId]);
            ImmutableArray<WindowOutcome> restored = await system.ApplyAsync(restore, Approvals(restore));
            ImmutableArray<WindowSnapshot> afterRestore = (await system.CaptureAsync()).Windows
                .Where(window => window.Application.ApplicationId == "org.windowrecall.macosfixture")
                .OrderBy(window => window.NormalBounds.X)
                .ThenBy(window => window.NormalBounds.Y)
                .ToImmutableArray();
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                capturedWindowCount = windows.Length,
                captured = windows.Select(window => new { window.WindowId, observedBefore = window.NormalBounds, window.State }),
                move = move.Items.Zip(afterMove, (requested, observed) => new
                {
                    requested.SavedWindowId,
                    requested = requested.TargetBounds,
                    observedBefore = windows.Single(window => $"saved-{window.WindowId}" == requested.SavedWindowId).NormalBounds,
                    observedAfter = observed.NormalBounds,
                    outcome = moved.Single(value => value.SavedWindowId == requested.SavedWindowId).Code,
                }),
                restore = restore.Items.Zip(afterRestore, (requested, observed) => new
                {
                    requested.SavedWindowId,
                    requested = requested.TargetBounds,
                    observedBefore = afterMove.Single(window => $"saved-{window.WindowId}" == requested.SavedWindowId).NormalBounds,
                    observedAfter = observed.NormalBounds,
                    outcome = restored.Single(value => value.SavedWindowId == requested.SavedWindowId).Code,
                }),
            }, EvidenceJsonOptions));
            Assert.All(moved, outcome => Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code));
            Assert.All(restored, outcome => Assert.Equal(WindowOutcomeCode.Succeeded, outcome.Code));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} for the explicitly opted-in live harness.");

    private static RestorePlan Plan(ImmutableArray<WindowSnapshot> windows, Func<WindowSnapshot, DesktopRect> target) =>
        new(Guid.NewGuid(), windows.Select(window => new RestorePlanItem($"saved-{window.WindowId}", window.WindowId,
            RestoreAction.MoveResize, target(window), null, "fixture-approved")).ToImmutableArray(),
            new CurrentDesktop(DateTimeOffset.UtcNow, [], windows));

    private static ImmutableArray<string> Approvals(RestorePlan plan) => plan.Items.Select(item => item.SavedWindowId).ToImmutableArray();
}

internal sealed class MacOSDesktopFactAttribute : FactAttribute
{
    public MacOSDesktopFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires macOS.";
        else if (!Environment.UserInteractive) Skip = "Requires an interactive desktop.";
        else if (!string.Equals(Environment.GetEnvironmentVariable("WINDOW_RECALL_RUN_MACOS_INTEGRATION"), "1", StringComparison.Ordinal))
            Skip = "Opt in with WINDOW_RECALL_RUN_MACOS_INTEGRATION=1 after granting Accessibility permission.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDOW_RECALL_MACOS_FIXTURE")) ||
                 string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDOW_RECALL_MACOS_EVIDENCE")))
            Skip = "Set WINDOW_RECALL_MACOS_FIXTURE and WINDOW_RECALL_MACOS_EVIDENCE.";
        else if (!new MacOSNativeApi().GetAccessibilityTrust())
            Skip = "Accessibility permission is not granted to this test host.";
    }
}
