using System.Globalization;
using WindowRecall.Core;

namespace WindowRecall.App.Tests;

/// <summary>View-model workflow tests. All desktop observation goes through the injected fake
/// adapter; these are NOT proof that a real window moved on a real desktop.</summary>
public sealed class MainWindowViewModelTests
{
    private static (MainWindowViewModel Vm, InMemoryProfileStore Store, FakeWindowSystem Desktop) Create(
        WindowSystemCapabilities? capabilities = null)
    {
        InMemoryProfileStore store = new();
        FakeWindowSystem desktop = new(AppFixture.TwoWindowDesktop(), capabilities);
        MainWindowViewModel vm = new(store, () => desktop);
        return (vm, store, desktop);
    }

    [Fact]
    public async Task Initialize_LoadsProfilesAndPermissionBanner()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, _) = Create();
        await store.SaveAsync("desk", AppFixture.CapturedProfile());
        await vm.InitializeAsync();

        Assert.Equal(["desk"], vm.Profiles);
        Assert.Equal(AppView.Home, vm.CurrentView);
        Assert.Equal("Local only", vm.PermissionTitle);
        // Platform-appropriate reassurance; assert the shared promise, not the OS-specific wording.
        Assert.Contains("never requests", vm.PermissionDetail, StringComparison.Ordinal);
        Assert.Contains("administrator rights", vm.PermissionDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartCapture_PopulatesReviewRowsWithoutTouchingTitlesWhenOff()
    {
        (MainWindowViewModel vm, _, FakeWindowSystem desktop) = Create();
        await vm.InitializeAsync();
        await vm.StartCaptureAsync();

        Assert.Equal(AppView.CaptureReview, vm.CurrentView);
        Assert.Equal(2, vm.CaptureWindows.Count);
        Assert.Contains("2 window(s)", vm.StatusMessage);
        Assert.Equal(1, desktop.CaptureCalls);
    }

    [Fact]
    public async Task SaveCapturedProfile_RespectsExclusionAndSlugCollision()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, _) = Create();
        await vm.InitializeAsync();
        await vm.StartCaptureAsync();
        vm.ProfileName = "Desk Setup!";
        vm.CaptureWindows.Single(w => w.SavedWindowId == "win-browser").IsExcluded = true;
        await vm.SaveCapturedProfileAsync();

        Assert.Equal(AppView.Home, vm.CurrentView);
        Assert.Equal(["desk-setup"], vm.Profiles);
        Assert.Equal("desk-setup", vm.SelectedProfileId);

        vm.ProfileName = "Desk Setup";
        await vm.StartCaptureAsync();
        vm.ProfileName = "Desk Setup!";
        vm.CaptureWindows.Single(w => w.SavedWindowId == "win-browser").IsExcluded = true;
        await vm.SaveCapturedProfileAsync();
        Assert.Equal(["desk-setup", "desk-setup-2"], vm.Profiles);

        LayoutProfile saved = await store.LoadAsync("desk-setup");
        Assert.Single(saved.Windows);
        Assert.Equal("com.example.editor", saved.Windows.Single().Application.ApplicationId);
    }

    [Fact]
    public async Task SaveCapturedProfile_TitlesOffByDefaultDropsEveryTitle()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, _) = Create();
        await vm.InitializeAsync();
        await vm.StartCaptureAsync();
        vm.ProfileName = "Desk";
        await vm.SaveCapturedProfileAsync();

        string json = store.Serialized["desk"];
        Assert.DoesNotContain("notes.txt", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("win-editor", json, StringComparison.Ordinal); // identity survives, content does not
        LayoutProfile saved = await store.LoadAsync("desk");
        Assert.False(saved.Privacy.PersistWindowTitles);
        Assert.All(saved.Windows, window => Assert.Null(window.Title));
    }

    [Fact]
    public async Task SaveCapturedProfile_TitlesOnAreRedactedAtSaveTime()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, _) = Create();
        await vm.InitializeAsync();
        await vm.StartCaptureAsync();
        vm.ProfileName = "Desk";
        vm.PersistWindowTitles = true;
        vm.RedactionPatternsText = "notes\\.txt";
        await vm.SaveCapturedProfileAsync();

        string json = store.Serialized["desk"];
        Assert.Contains(TitleRedactor.Marker, json, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveCapturedProfile_InvalidRedactionPatternIsAnnouncedAndRejected()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, _) = Create();
        await vm.InitializeAsync();
        await vm.StartCaptureAsync();
        vm.ProfileName = "Desk";
        vm.PersistWindowTitles = true;
        vm.RedactionPatternsText = "([unclosed";
        await vm.SaveCapturedProfileAsync();

        Assert.Empty(vm.Profiles);
        Assert.Contains("not a valid redaction pattern", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editor_PersistsLaunchPolicyAndExclusions()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, _) = Create();
        await store.SaveAsync("desk", AppFixture.CapturedProfile());
        await vm.InitializeAsync();
        vm.SelectedProfileId = "desk";
        await vm.EditProfileAsync("desk");

        Assert.Equal(AppView.ProfileEditor, vm.CurrentView);
        vm.EditorWindows.Single(w => w.SavedWindowId == "win-browser").LaunchPolicy = LaunchPolicyRow.AllowExplicitLaunch;
        vm.EditorWindows.Single(w => w.SavedWindowId == "win-editor").IsExcluded = true;
        await vm.SaveEditorAsync();

        LayoutProfile saved = await store.LoadAsync("desk");
        WindowSnapshot browser = saved.Windows.Single(w => w.WindowId == "win-browser");
        Assert.Equal(LaunchPolicy.AllowExplicitLaunch, browser.LaunchPolicy);
        Assert.Single(saved.Windows);
    }

    [Fact]
    public async Task Preview_ListsEveryPlannedActionAndNeverSelectsAmbiguousAutomatically()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, FakeWindowSystem desktop) = Create();
        await store.SaveAsync("desk", AppFixture.CapturedProfile());
        // Add a second identical-identity window so the editor match becomes ambiguous.
        desktop.SetDesktop(new CurrentDesktop(
            DateTimeOffset.UtcNow,
            [AppFixture.Display1, AppFixture.Display2],
            [AppFixture.Editor("win-editor-a"), AppFixture.Editor("win-editor-b"), AppFixture.Browser()]));
        await vm.InitializeAsync();
        vm.SelectedProfileId = "desk";
        await vm.PreparePreviewAsync("desk");

        Assert.Equal(AppView.RestorePreview, vm.CurrentView);
        Assert.Equal(2, vm.PreviewRows.Count);
        PreviewItemRow ambiguous = vm.PreviewRows.Single(r => r.SavedWindowId == "win-editor");
        Assert.Equal("Needs review", ambiguous.ActionLabel);
        Assert.False(ambiguous.CanSelect);
        Assert.False(ambiguous.IsSelected);
        Assert.Equal(2, ambiguous.CandidateOptions.Length);
        PreviewItemRow browser = vm.PreviewRows.Single(r => r.SavedWindowId == "win-browser");
        Assert.Equal("Move and resize", browser.ActionLabel);
        Assert.Contains("1 actionable", vm.PreviewSummary, StringComparison.Ordinal);
        Assert.Contains("1 need review", vm.PreviewSummary, StringComparison.Ordinal);
        Assert.Contains("No window has moved", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0, desktop.ApplyCalls);
    }

    [Fact]
    public async Task Preview_DisplaysSourceAndDestinationMonitorPerWindow()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, FakeWindowSystem desktop) = Create();
        await store.SaveAsync("desk", AppFixture.CapturedProfile());
        await vm.InitializeAsync();
        vm.SelectedProfileId = "desk";
        await vm.PreparePreviewAsync("desk");

        PreviewItemRow editor = vm.PreviewRows.Single(r => r.SavedWindowId == "win-editor");
        Assert.Contains("Built-in", editor.SourceDisplay, StringComparison.Ordinal);
        Assert.Contains("Built-in", editor.DestinationDisplay, StringComparison.Ordinal);
        Assert.Contains("x=", editor.TargetBoundsSummary, StringComparison.Ordinal);
        PreviewItemRow browser = vm.PreviewRows.Single(r => r.SavedWindowId == "win-browser");
        Assert.Contains("Built-in", browser.SourceDisplay, StringComparison.Ordinal);
        Assert.Equal(0, desktop.ApplyCalls);
    }

    [Fact]
    public async Task MultiWindowApply_RequiresConfirmationAndMovesNothingBeforeConfirm()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, FakeWindowSystem desktop) = Create();
        await store.SaveAsync("desk", AppFixture.CapturedProfile());
        await vm.InitializeAsync();
        vm.SelectedProfileId = "desk";
        await vm.PreparePreviewAsync("desk");
        foreach (PreviewItemRow row in vm.PreviewRows.Where(r => r.CanSelect))
            row.IsSelected = true;
        Assert.Equal(2, vm.SelectedPreviewCount);

        vm.RequestApply();
        Assert.Equal(ConfirmationContext.Apply, vm.Confirmation);
        Assert.Contains("Apply 2", vm.ConfirmationText, StringComparison.Ordinal);
        Assert.Equal(0, desktop.ApplyCalls);

        vm.Decline();
        Assert.Equal(ConfirmationContext.None, vm.Confirmation);
        Assert.Equal(0, desktop.ApplyCalls);
        Assert.Contains("Nothing changed", vm.StatusMessage, StringComparison.Ordinal);

        vm.RequestApply();
        await vm.ConfirmAsync();
        Assert.Equal(AppView.ApplyResult, vm.CurrentView);
        Assert.Contains("2 succeeded", vm.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousItem_OnlyAppliesAfterExplicitResolutionAndClampsOnScreen()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, FakeWindowSystem desktop) = Create();
        await store.SaveAsync("desk", AppFixture.CapturedProfile());
        desktop.SetDesktop(new CurrentDesktop(
            DateTimeOffset.UtcNow,
            [AppFixture.Display1, AppFixture.Display2],
            [AppFixture.Editor("win-editor-a"), AppFixture.Editor("win-editor-b"), AppFixture.Browser()]));
        await vm.InitializeAsync();
        vm.SelectedProfileId = "desk";
        await vm.PreparePreviewAsync("desk");

        PreviewItemRow ambiguous = vm.PreviewRows.Single(r => r.SavedWindowId == "win-editor");
        ambiguous.ResolvedOption = ambiguous.CandidateOptions.Single(o => o.CurrentWindowId == "win-editor-b");
        Assert.True(ambiguous.CanSelect);
        ambiguous.IsSelected = true;

        await vm.RunApplyAsync();

        Assert.Equal(AppView.ApplyResult, vm.CurrentView);
        Assert.Contains("1 succeeded", vm.ResultSummary, StringComparison.Ordinal);
        DesktopRect applied = desktop.AppliedBounds.Single();
        DesktopRect work = AppFixture.Display1.WorkArea;
        Assert.InRange(applied.X, work.X - applied.Width + RestoreGeometry.MinimumVisibleWidth, work.X + work.Width - RestoreGeometry.MinimumVisibleWidth);
    }

    [Fact]
    public async Task PartialFailure_ReportsPerWindowAndKeepsUndoAvailable()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, FakeWindowSystem desktop) = Create();
        await store.SaveAsync("desk", AppFixture.CapturedProfile());
        desktop.OutcomeOverride = item => item.SavedWindowId == "win-browser"
            ? new WindowOutcome(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Failed, "The app rejected the move (simulated).")
            : new WindowOutcome(item.SavedWindowId, item.CurrentWindowId, WindowOutcomeCode.Succeeded, "Fake applied.");
        await vm.InitializeAsync();
        vm.SelectedProfileId = "desk";
        await vm.PreparePreviewAsync("desk");
        foreach (PreviewItemRow row in vm.PreviewRows.Where(r => r.CanSelect))
            row.IsSelected = true;
        await vm.RunApplyAsync();

        Assert.Contains("1 succeeded, 1 not completed", vm.ResultSummary, StringComparison.Ordinal);
        Assert.Contains(vm.ResultRows, r => r.CodeLabel == "FAILED");
        Assert.Contains(vm.ResultRows, r => r.CodeLabel == "OK");
        Assert.True(vm.UndoAvailable);
        Assert.True(vm.IsUndoVisible);
    }

    [Fact]
    public async Task Undo_UsesPreApplySnapshotAndClearsAvailability()
    {
        (MainWindowViewModel vm, InMemoryProfileStore store, FakeWindowSystem desktop) = Create();
        await store.SaveAsync("desk", AppFixture.CapturedProfile());
        await vm.InitializeAsync();
        vm.SelectedProfileId = "desk";
        await vm.PreparePreviewAsync("desk");
        foreach (PreviewItemRow row in vm.PreviewRows.Where(r => r.CanSelect))
            row.IsSelected = true;
        await vm.RunApplyAsync();
        int callsBeforeUndo = desktop.ApplyCalls;

        await vm.UndoAsync();

        Assert.Equal(callsBeforeUndo + 2, desktop.ApplyCalls);
        Assert.False(vm.UndoAvailable);
        Assert.Contains("Undo complete", vm.ResultSummary, StringComparison.Ordinal);
        Assert.Contains(vm.LastUndoOutcomes, o => o.Code == WindowOutcomeCode.Succeeded);
    }

    [Fact]
    public async Task Cancel_DuringApply_ReportsCancelledOutcomeAndKeepsUndoForCompletedItems()
    {
        // Explicitly NOT on the UI-thread SynchronizationContext so a mid-apply wait cannot deadlock.
        Task run = Task.Run(async () =>
        {
            (MainWindowViewModel vm, InMemoryProfileStore store, FakeWindowSystem desktop) = Create();
            await store.SaveAsync("desk", AppFixture.CapturedProfile());
            await vm.InitializeAsync();
            vm.SelectedProfileId = "desk";
            await vm.PreparePreviewAsync("desk");
            TaskCompletionSource releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
            desktop.DelayPerItem = async (item, token) =>
            {
                // win-editor is applied second (ordinal plan order), so the browser succeeds first.
                if (item.SavedWindowId == "win-editor")
                    await releaseSecond.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                await Task.Yield();
            };
            foreach (PreviewItemRow row in vm.PreviewRows.Where(r => r.CanSelect))
                row.IsSelected = true;

            Task apply = vm.RunApplyAsync();
            await WaitUntil(() => desktop.ApplyCalls == 2);
            vm.CancelApplyCommand.Execute(null);
            releaseSecond.SetResult();
            await apply;

            Assert.Contains("Restore cancelled", vm.ResultSummary, StringComparison.Ordinal);
            Assert.Contains(vm.ResultRows, r => r.CodeLabel == "CANCELLED");
            Assert.Contains(vm.ResultRows, r => r.CodeLabel == "OK");
            Assert.True(vm.UndoAvailable);
        });
        await run;
    }

    [Fact]
    public async Task UnsupportedDesktop_ShowsNonTrappingNoticeAndNavigationReturnsHome()
    {
        InMemoryProfileStore store = new();
        UnsupportedFakeDesktop fake = new();
        MainWindowViewModel vm = new(store, () => fake);
        await vm.InitializeAsync();
        await vm.StartCaptureAsync();

        Assert.Equal(AppView.PermissionNotice, vm.CurrentView);
        Assert.Contains("return to profiles at any time", vm.StatusMessage, StringComparison.Ordinal);
        vm.GoHome();
        Assert.Equal(AppView.Home, vm.CurrentView);
    }

    [Fact]
    public async Task PermissionProvider_FailureDoesNotBreakInitialization()
    {
        InMemoryProfileStore store = new();
        FakeWindowSystem desktop = new(AppFixture.TwoWindowDesktop());
        MainWindowViewModel vm = new(store, () => desktop, new ThrowingPermissionProvider());
        await vm.InitializeAsync();

        Assert.Equal("Permission status unavailable", vm.PermissionTitle);
        Assert.Equal(AppView.Home, vm.CurrentView);
    }

    [Fact]
    public void Slugify_ProducesSafeProfileIds()
    {
        Assert.Equal("desk-setup", MainWindowViewModel.Slugify("Desk Setup!"));
        Assert.Equal("a-b", MainWindowViewModel.Slugify("  ///a///b/// "));
        Assert.Equal("profile", MainWindowViewModel.Slugify("!!!"));
        Assert.Equal(48, MainWindowViewModel.Slugify(new string('x', 100)).Length);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        foreach (int _ in Enumerable.Range(0, 200))
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        Assert.Fail("Condition was not met within the timeout.");
    }

    /// <summary>Fake adapter that reports an unavailable desktop, like a headless Linux session.</summary>
    private sealed class UnsupportedFakeDesktop : IWindowSystem
    {
        public Task<CurrentDesktop> CaptureAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<CurrentDesktop>(new PlatformNotSupportedException(
                "No Window Recall desktop integration is available on this platform or session."));

        public Task<System.Collections.Immutable.ImmutableArray<WindowOutcome>> ApplyAsync(
            RestorePlan plan, System.Collections.Immutable.ImmutableArray<string> approvedSavedWindowIds,
            CancellationToken cancellationToken = default) =>
            throw new PlatformNotSupportedException();

        public Task<WindowSystemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowSystemCapabilities(false, false, false, false, "Headless session."));
    }

    private sealed class ThrowingPermissionProvider : IPermissionStatusProvider
    {
        public Task<PermissionBanner> GetBannerAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<PermissionBanner>(new InvalidOperationException("AX query failed (simulated)."));

        public Task<bool> OpenSystemSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
