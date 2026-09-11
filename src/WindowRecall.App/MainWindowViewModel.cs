using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using WindowRecall.Core;

namespace WindowRecall.App;

/// <summary>Top-level UI states. Every state is reachable from navigation so no state
/// (including permission notices) can trap the user.</summary>
public enum AppView
{
    Home,
    CaptureReview,
    ProfileEditor,
    RestorePreview,
    ApplyResult,
    PermissionNotice,
}

/// <summary>Explicit confirmation contexts so multi-window applies and destructive deletes require a
/// second, deliberate action instead of an unlabelled dialog.</summary>
public enum ConfirmationContext
{
    None,
    Apply,
    Delete,
}

/// <summary>Platform permission/limitations banner surfaced without blocking navigation.</summary>
public sealed record PermissionBanner(string Title, string Detail, bool CanOpenSettings);

/// <summary>Provides the platform permission banner without forcing App logic to reference native APIs.</summary>
public interface IPermissionStatusProvider
{
    Task<PermissionBanner> GetBannerAsync(CancellationToken cancellationToken = default);

    Task<bool> OpenSystemSettingsAsync(CancellationToken cancellationToken = default);
}

/// <summary>One selectable resolution for an ambiguous preview row.</summary>
public sealed record AmbiguityOption(string CurrentWindowId, string Label);

/// <summary>Root view model for the single-window workflow: profiles, capture review, editor,
/// restore preview with ambiguity resolution, confirmed apply with cancel, partial results, and undo.</summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan RedactionProbeTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IProfileStore profiles;
    private readonly Func<IWindowSystem> windowSystemFactory;
    private readonly IPermissionStatusProvider? permissionProvider;
    private readonly IRestorePlanner planner;
    private readonly DeterministicWindowMatcher matcher = new();
    private readonly DisplayTopologyMapper topologyMapper = new();

    private IWindowSystem? windowSystem;
    private RestoreCoordinator? coordinator;
    private CancellationTokenSource? applyCancellation;
    private CurrentDesktop? captureDesktop;
    private RestorePlan? currentPlan;
    private LayoutProfile? previewedProfile;
    private UndoReceipt? lastReceipt;
    private ImmutableArray<WindowOutcome> lastUndoOutcomes;

    private AppView currentView = AppView.Home;
    private string statusMessage = "Ready. Choose a profile or capture the current desktop.";
    private string? errorMessage;
    private string permissionTitle = string.Empty;
    private string permissionDetail = string.Empty;
    private bool permissionCanOpenSettings;

    private string? selectedProfileId;
    private string profileName = string.Empty;
    private bool persistWindowTitles;
    private string redactionPatternsText = string.Empty;
    private string editorProfileId = string.Empty;
    private string editorProfileName = string.Empty;
    private bool editorPersistWindowTitles;
    private string editorRedactionPatternsText = string.Empty;

    private ConfirmationContext confirmation;
    private string confirmationText = string.Empty;
    private bool undoAvailable;
    private string resultSummary = string.Empty;
    private string previewSummary = string.Empty;
    private string topologySummary = "Not inspected yet.";

    public MainWindowViewModel(
        IProfileStore profiles,
        Func<IWindowSystem> windowSystemFactory,
        IPermissionStatusProvider? permissionProvider = null,
        IRestorePlanner? planner = null)
    {
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.windowSystemFactory = windowSystemFactory ?? throw new ArgumentNullException(nameof(windowSystemFactory));
        this.permissionProvider = permissionProvider;
        this.planner = planner ?? new RestorePlanner();
    }

    // -------- observable collections --------

    public ObservableCollection<string> Profiles { get; } = new();

    public ObservableCollection<WindowToggleItem> CaptureWindows { get; } = new();

    public ObservableCollection<WindowToggleItem> EditorWindows { get; } = new();

    public ObservableCollection<CurrentTopologyRow> TopologyRows { get; } = new();

    public ObservableCollection<PreviewItemRow> PreviewRows { get; } = new();

    public ObservableCollection<OutcomeRow> ResultRows { get; } = new();

    public ProgressState Progress { get; } = new();

    // -------- commands --------

    public RelayCommand NavigateHomeCommand { get; private set; } = null!;

    public RelayCommand DeclineCommand { get; private set; } = null!;

    public RelayCommand CancelApplyCommand { get; private set; } = null!;

    public AsyncCommand StartCaptureCommand { get; private set; } = null!;

    public AsyncCommand SaveCapturedProfileCommand { get; private set; } = null!;

    public AsyncCommand EditSelectedProfileCommand { get; private set; } = null!;

    public AsyncCommand SaveEditorCommand { get; private set; } = null!;

    public AsyncCommand DeleteSelectedProfileCommand { get; private set; } = null!;

    public AsyncCommand PreviewSelectedProfileCommand { get; private set; } = null!;

    public AsyncCommand UndoCommand { get; private set; } = null!;

    public AsyncCommand OpenPermissionSettingsCommand { get; private set; } = null!;

    public AsyncCommand ConfirmCommand { get; private set; } = null!;

    public AsyncCommand RefreshProfilesCommand { get; private set; } = null!;

    public RelayCommand RequestDeleteCommand { get; private set; } = null!;

    public RelayCommand RequestApplyCommand { get; private set; } = null!;

    public ICommand ApplySelectedCommand => applyCommand;

    private AsyncCommand applyCommand = null!;

    public bool IsBusy => Progress.IsBusy;

    internal void InitializeCommands()
    {
        NavigateHomeCommand = new RelayCommand(() => GoHome());
        DeclineCommand = new RelayCommand(Decline, () => Confirmation is not ConfirmationContext.None);
        CancelApplyCommand = new RelayCommand(CancelApply, () => IsBusy);
        StartCaptureCommand = new AsyncCommand(_ => StartCaptureAsync(CancellationToken.None), () => !IsBusy);
        SaveCapturedProfileCommand = new AsyncCommand(_ => SaveCapturedProfileAsync(CancellationToken.None), () => !IsBusy);
        EditSelectedProfileCommand = new AsyncCommand(_ => EditProfileAsync(SelectedProfileId, CancellationToken.None), () => SelectedProfileId is not null && !IsBusy);
        SaveEditorCommand = new AsyncCommand(_ => SaveEditorAsync(CancellationToken.None), () => !IsBusy);
        DeleteSelectedProfileCommand = new AsyncCommand(_ => DeleteSelectedProfileAsync(CancellationToken.None), () => SelectedProfileId is not null && !IsBusy);
        PreviewSelectedProfileCommand = new AsyncCommand(_ => PreparePreviewAsync(SelectedProfileId, CancellationToken.None), () => SelectedProfileId is not null && !IsBusy);
        applyCommand = new AsyncCommand(_ => RunApplyAsync(), () => !IsBusy && SelectedPreviewCount > 0);
        UndoCommand = new AsyncCommand(_ => UndoAsync(CancellationToken.None), () => UndoAvailable && !IsBusy);
        OpenPermissionSettingsCommand = new AsyncCommand(_ => OpenPermissionSettingsAsync(CancellationToken.None), () => permissionProvider is not null);
        ConfirmCommand = new AsyncCommand(_ => ConfirmAsync(CancellationToken.None), () => Confirmation is not ConfirmationContext.None);
        RefreshProfilesCommand = new AsyncCommand(_ => RefreshProfilesCoreAsync(CancellationToken.None));
        RequestDeleteCommand = new RelayCommand(RequestDelete, () => SelectedProfileId is not null);
        RequestApplyCommand = new RelayCommand(RequestApply, () => SelectedPreviewCount > 0);
    }

    private void RefreshCommandGuards()
    {
        StartCaptureCommand.RaiseCanExecuteChanged();
        SaveCapturedProfileCommand.RaiseCanExecuteChanged();
        EditSelectedProfileCommand.RaiseCanExecuteChanged();
        SaveEditorCommand.RaiseCanExecuteChanged();
        DeleteSelectedProfileCommand.RaiseCanExecuteChanged();
        PreviewSelectedProfileCommand.RaiseCanExecuteChanged();
        applyCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        OpenPermissionSettingsCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
        CancelApplyCommand.RaiseCanExecuteChanged();
        DeclineCommand.RaiseCanExecuteChanged();
        RefreshProfilesCommand.RaiseCanExecuteChanged();
        RequestDeleteCommand.RaiseCanExecuteChanged();
        RequestApplyCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsBusy));
    }

    // -------- view state --------

    public AppView CurrentView
    {
        get => currentView;
        private set
        {
            if (SetProperty(ref currentView, value))
            {
                OnPropertyChanged(nameof(IsHomeView));
                OnPropertyChanged(nameof(IsCaptureView));
                OnPropertyChanged(nameof(IsEditorView));
                OnPropertyChanged(nameof(IsPreviewView));
                OnPropertyChanged(nameof(IsResultView));
                OnPropertyChanged(nameof(IsPermissionView));
            }
        }
    }

    public bool IsHomeView => CurrentView is AppView.Home;

    public bool IsCaptureView => CurrentView is AppView.CaptureReview;

    public bool IsEditorView => CurrentView is AppView.ProfileEditor;

    public bool IsPreviewView => CurrentView is AppView.RestorePreview;

    public bool IsResultView => CurrentView is AppView.ApplyResult;

    public bool IsPermissionView => CurrentView is AppView.PermissionNotice;

    /// <summary>Assertive live-region text announced to screen readers on every state transition.</summary>
    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }

    public string PermissionTitle
    {
        get => permissionTitle;
        private set => SetProperty(ref permissionTitle, value);
    }

    public string PermissionDetail
    {
        get => permissionDetail;
        private set => SetProperty(ref permissionDetail, value);
    }

    public bool PermissionCanOpenSettings
    {
        get => permissionCanOpenSettings;
        private set => SetProperty(ref permissionCanOpenSettings, value);
    }

    public string? SelectedProfileId
    {
        get => selectedProfileId;
        set
        {
            if (SetProperty(ref selectedProfileId, value))
                RefreshCommandGuards();
        }
    }

    public string ProfileName
    {
        get => profileName;
        set => SetProperty(ref profileName, value);
    }

    public bool PersistWindowTitles
    {
        get => persistWindowTitles;
        set => SetProperty(ref persistWindowTitles, value);
    }

    public string RedactionPatternsText
    {
        get => redactionPatternsText;
        set => SetProperty(ref redactionPatternsText, value);
    }

    public string EditorProfileKey => editorProfileId;

    public string EditorProfileDisplayName
    {
        get => editorProfileName;
        set
        {
            SetProperty(ref editorProfileName, value);
            OnPropertyChanged(nameof(EditorProfileKey));
        }
    }

    public bool EditorPersistWindowTitles
    {
        get => editorPersistWindowTitles;
        set => SetProperty(ref editorPersistWindowTitles, value);
    }

    public string EditorRedactionPatternsText
    {
        get => editorRedactionPatternsText;
        set => SetProperty(ref editorRedactionPatternsText, value);
    }

    public ConfirmationContext Confirmation
    {
        get => confirmation;
        private set
        {
            SetProperty(ref confirmation, value);
            OnPropertyChanged(nameof(IsConfirmationVisible));
            RefreshCommandGuards();
        }
    }

    public bool IsConfirmationVisible => Confirmation is not ConfirmationContext.None;

    public string ConfirmationText
    {
        get => confirmationText;
        private set => SetProperty(ref confirmationText, value);
    }

    public bool UndoAvailable
    {
        get => undoAvailable;
        private set
        {
            SetProperty(ref undoAvailable, value);
            OnPropertyChanged(nameof(IsUndoVisible));
            RefreshCommandGuards();
        }
    }

    public bool IsUndoVisible => UndoAvailable;

    public string ResultSummary
    {
        get => resultSummary;
        private set => SetProperty(ref resultSummary, value);
    }

    public string PreviewSummary
    {
        get => previewSummary;
        private set => SetProperty(ref previewSummary, value);
    }

    public string TopologySummary
    {
        get => topologySummary;
        private set => SetProperty(ref topologySummary, value);
    }

    public int SelectedPreviewCount => PreviewRows.Count(row => row.IsSelected);

    public ImmutableArray<WindowOutcome> LastUndoOutcomes => lastUndoOutcomes;

    // -------- boot --------

    /// <summary>Loads profile names and the permission banner without ever moving a window.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        InitializeCommands();
        await RefreshProfilesCoreAsync(cancellationToken).ConfigureAwait(false);
        await RefreshPermissionBannerAsync(cancellationToken).ConfigureAwait(false);
        RefreshCommandGuards();
    }

    // -------- home: profiles + topology --------

    public async Task RefreshProfilesCoreAsync(CancellationToken cancellationToken)
    {
        ImmutableArray<string> ids = await profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        Profiles.Clear();
        foreach (string id in ids)
            Profiles.Add(id);
        RefreshCommandGuards();
    }

    public async Task RefreshTopologySummaryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            CurrentDesktop desktop = await CaptureDesktopAsync(cancellationToken).ConfigureAwait(false);
            TopologyRows.Clear();
            foreach (DisplaySnapshot display in desktop.Displays)
                TopologyRows.Add(new CurrentTopologyRow(display.Id, DescribeDisplay(display), display.IsPrimary));
            TopologySummary = $"{desktop.Displays.Length} display(s) attached.";
        }
        catch (PlatformNotSupportedException exception)
        {
            TopologyRows.Clear();
            TopologySummary = exception.Message;
        }
    }

    // -------- capture + review --------

    public async Task StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        CurrentView = AppView.Home;
        CurrentDesktop desktop;
        try
        {
            desktop = await CaptureDesktopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException exception)
        {
            ShowPermissionNotice(exception.Message);
            return;
        }

        captureDesktop = desktop;
        CaptureWindows.Clear();
        Dictionary<string, DisplaySnapshot> byId = desktop.Displays.ToDictionary(display => display.Id, StringComparer.Ordinal);
        foreach (WindowSnapshot window in desktop.Windows.OrderBy(w => w.WindowId, StringComparer.Ordinal))
        {
            string displayLabel = window.DisplayId is not null && byId.TryGetValue(window.DisplayId, out DisplaySnapshot? display)
                ? DescribeDisplay(display)
                : "unknown display";
            CaptureWindows.Add(new WindowToggleItem(window.WindowId, window.Application.ApplicationId, window.Title, displayLabel)
            {
                LaunchPolicy = Map(window.LaunchPolicy),
            });
        }

        ProfileName = string.Empty;
        PersistWindowTitles = false;
        RedactionPatternsText = string.Empty;
        CurrentView = AppView.CaptureReview;
        Announce($"Captured {desktop.Windows.Length} window(s) on {desktop.Displays.Length} display(s). Review which windows to include before saving.");
        RefreshCommandGuards();
    }

    public async Task SaveCapturedProfileAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        if (captureDesktop is null)
        {
            Announce("Nothing has been captured yet.");
            return;
        }
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            Announce("A profile name is required before saving.");
            return;
        }

        ImmutableArray<string> patterns = ParsePatterns(RedactionPatternsText);
        if (!TryValidatePatterns(patterns))
            return;

        ImmutableArray<WindowSnapshot>.Builder included = ImmutableArray.CreateBuilder<WindowSnapshot>();
        Dictionary<string, WindowSnapshot> observed = captureDesktop.Windows.ToDictionary(w => w.WindowId, StringComparer.Ordinal);
        foreach (WindowToggleItem row in CaptureWindows)
        {
            if (row.IsExcluded || !observed.TryGetValue(row.SavedWindowId, out WindowSnapshot? source))
                continue;
            // Raw titles are attached only when opted in; Core redacts at serialization time.
            string? storedTitle = PersistWindowTitles ? source.Title : null;
            included.Add(source with { Title = storedTitle, LaunchPolicy = Unmap(row.LaunchPolicy) });
        }

        if (included.Count == 0)
        {
            Announce("Include at least one window in the profile.");
            return;
        }

        LayoutProfile profile = new(
            LayoutProfile.CurrentSchemaVersion,
            ProfileName.Trim(),
            DateTimeOffset.UtcNow,
            captureDesktop.Displays,
            included.ToImmutable(),
            new ProfilePrivacy(PersistWindowTitles) { RedactionPatterns = patterns });

        try
        {
            string id = await NextProfileIdAsync(profile.Name, cancellationToken).ConfigureAwait(false);
            await profiles.SaveAsync(id, profile, cancellationToken).ConfigureAwait(false);
            await RefreshProfilesCoreAsync(cancellationToken).ConfigureAwait(false);
            SelectedProfileId = id;
            GoHome();
            Announce($"Saved profile '{profile.Name}' with {included.Count} window(s) as '{id}'.");
        }
        catch (ProfileFormatException exception)
        {
            Announce($"The profile was rejected: {exception.Message}");
        }
    }

    // -------- editor --------

    public async Task EditProfileAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        if (profileId is null)
            return;
        LayoutProfile profile = await profiles.LoadAsync(profileId, cancellationToken).ConfigureAwait(false);
        editorProfileId = profileId;
        EditorProfileDisplayName = profile.Name;
        EditorPersistWindowTitles = profile.Privacy.PersistWindowTitles;
        EditorRedactionPatternsText = string.Join('\n', profile.Privacy.RedactionPatterns);
        Dictionary<string, DisplaySnapshot> byId = profile.Displays.ToDictionary(display => display.Id, StringComparer.Ordinal);
        EditorWindows.Clear();
        foreach (WindowSnapshot window in profile.Windows.OrderBy(w => w.WindowId, StringComparer.Ordinal))
        {
            string displayLabel = window.DisplayId is not null && byId.TryGetValue(window.DisplayId, out DisplaySnapshot? display)
                ? DescribeDisplay(display)
                : "unknown display";
            EditorWindows.Add(new WindowToggleItem(window.WindowId, window.Application.ApplicationId, window.Title, displayLabel)
            {
                LaunchPolicy = Map(window.LaunchPolicy),
            });
        }

        CurrentView = AppView.ProfileEditor;
        Announce($"Editing profile '{profile.Name}'. Adjust window inclusion, launch policies, and privacy choices, then save.");
        RefreshCommandGuards();
    }

    public async Task SaveEditorAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        if (editorProfileId.Length == 0)
            return;
        LayoutProfile existing = await profiles.LoadAsync(editorProfileId, cancellationToken).ConfigureAwait(false);

        ImmutableArray<string> patterns = ParsePatterns(EditorRedactionPatternsText);
        if (!TryValidatePatterns(patterns))
            return;

        Dictionary<string, WindowSnapshot> existingById = existing.Windows.ToDictionary(w => w.WindowId, StringComparer.Ordinal);
        ImmutableArray<WindowSnapshot>.Builder kept = ImmutableArray.CreateBuilder<WindowSnapshot>();
        foreach (WindowToggleItem row in EditorWindows)
        {
            if (row.IsExcluded || !existingById.TryGetValue(row.SavedWindowId, out WindowSnapshot? source))
                continue;
            string? title = EditorPersistWindowTitles ? source.Title : null;
            kept.Add(source with { Title = title, LaunchPolicy = Unmap(row.LaunchPolicy) });
        }

        if (kept.Count == 0)
        {
            Announce("Keep at least one window in the profile.");
            return;
        }

        LayoutProfile updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(EditorProfileDisplayName) ? existing.Name : EditorProfileDisplayName.Trim(),
            Windows = kept.ToImmutable(),
            Privacy = new ProfilePrivacy(EditorPersistWindowTitles) { RedactionPatterns = patterns },
        };
        try
        {
            await profiles.SaveAsync(editorProfileId, updated, cancellationToken).ConfigureAwait(false);
            await RefreshProfilesCoreAsync(cancellationToken).ConfigureAwait(false);
            GoHome();
            Announce($"Profile '{updated.Name}' updated.");
        }
        catch (ProfileFormatException exception)
        {
            Announce($"The profile was rejected: {exception.Message}");
        }
    }

    public void RequestDelete()
    {
        if (SelectedProfileId is null)
            return;
        Confirmation = ConfirmationContext.Delete;
        ConfirmationText = $"Delete profile '{SelectedProfileId}'? Its JSON file will be removed from local storage.";
        Announce(ConfirmationText);
    }

    public async Task DeleteSelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedProfileId is null)
            return;
        string deleted = SelectedProfileId;
        await profiles.DeleteAsync(deleted, cancellationToken).ConfigureAwait(false);
        SelectedProfileId = null;
        await RefreshProfilesCoreAsync(cancellationToken).ConfigureAwait(false);
        Announce($"Profile '{deleted}' deleted.");
    }

    // -------- restore preview --------

    public async Task PreparePreviewAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        if (profileId is null)
            return;

        LayoutProfile profile = await profiles.LoadAsync(profileId, cancellationToken).ConfigureAwait(false);
        CurrentDesktop desktop;
        try
        {
            desktop = await CaptureDesktopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException exception)
        {
            ShowPermissionNotice(exception.Message);
            return;
        }

        WindowSystemCapabilities capabilities = await OpenWindowSystem().GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.CanObserve && !capabilities.CanMoveResize)
        {
            ShowPermissionNotice(capabilities.Limitation ?? "This platform cannot observe or move windows right now.");
            return;
        }

        RestorePlan plan = await planner.CreatePlanAsync(profile, desktop, cancellationToken).ConfigureAwait(false);
        ImmutableArray<WindowMatch> resolutions = await matcher.ResolveAsync(profile, desktop, cancellationToken).ConfigureAwait(false);
        Dictionary<string, WindowMatch> resolutionBySaved = resolutions.ToDictionary(m => m.SavedWindowId, StringComparer.Ordinal);

        currentPlan = plan;
        previewedProfile = profile;
        PreviewRows.Clear();

        Dictionary<string, WindowSnapshot> savedById = profile.Windows.ToDictionary(w => w.WindowId, StringComparer.Ordinal);
        Dictionary<string, DisplaySnapshot> currentById = desktop.Displays.ToDictionary(d => d.Id, StringComparer.Ordinal);
        Dictionary<string, DisplaySnapshot> profileDisplayById = profile.Displays.ToDictionary(d => d.Id, StringComparer.Ordinal);
        DisplaySnapshot? defaultSaved = profile.Displays.FirstOrDefault(d => d.IsPrimary) ?? profile.Displays.FirstOrDefault();

        int actionable = 0;
        int ambiguous = 0;
        int skips = 0;
        foreach (RestorePlanItem item in plan.Items)
        {
            WindowSnapshot saved = savedById[item.SavedWindowId];
            DisplaySnapshot? source = saved.DisplayId is not null && profileDisplayById.TryGetValue(saved.DisplayId, out DisplaySnapshot? specified)
                ? specified
                : defaultSaved;
            string sourceLabel = source is null ? "unknown display" : DescribeDisplay(source);
            string destinationLabel = "not available";
            if (source is not null)
            {
                ImmutableArray<DisplayMapping> mappings = await topologyMapper.MapDetailedAsync(profile.Displays, desktop.Displays, cancellationToken).ConfigureAwait(false);
                DisplayMapping? mapping = mappings.FirstOrDefault(m => m.SavedDisplayId == source.Id);
                if (mapping is not null && currentById.TryGetValue(mapping.CurrentDisplayId, out DisplaySnapshot? target))
                    destinationLabel = DescribeDisplay(target);
            }

            string appLabel = saved.Title is { Length: > 0 } title
                ? $"{saved.Application.ApplicationId} — {title}"
                : saved.Application.ApplicationId;
            string bounds = item.TargetBounds is null ? string.Empty : FormatBounds(item.TargetBounds);
            bool canSelect = item.CanAutoApply || item.Action is RestoreAction.Launch;
            string statusGlyph = item.Action switch
            {
                RestoreAction.Ambiguous => "[!]",
                RestoreAction.Skip => "[-]",
                RestoreAction.Launch => "[+]",
                _ => "[ ]",
            };
            string state = item.Action switch
            {
                RestoreAction.Ambiguous => "needs review and will not move automatically",
                RestoreAction.Skip => "will be skipped",
                RestoreAction.Launch => "launch requires explicit opt-in",
                _ => "planned",
            };

            var row = new PreviewItemRow(
                item.SavedWindowId,
                appLabel,
                ActionLabel(item.Action),
                sourceLabel,
                destinationLabel,
                bounds,
                item.Reason,
                isSelected: false,
                canSelect: canSelect,
                statusGlyph,
                $"{appLabel}. {ActionLabel(item.Action)}. From {sourceLabel} to {destinationLabel}. {state}.")
            {
                CandidateOptions = BuildCandidates(resolutionBySaved.TryGetValue(item.SavedWindowId, out WindowMatch? match) ? match : null, desktop),
            };
            row.PropertyChanged += PreviewRow_PropertyChanged;
            PreviewRows.Add(row);

            switch (item.Action)
            {
                case RestoreAction.Ambiguous: ambiguous++; break;
                case RestoreAction.Skip: skips++; break;
                default: actionable++; break;
            }
        }

        Confirmation = ConfirmationContext.None;
        PreviewSummary = $"Planned: {actionable} actionable, {ambiguous} need review, {skips} skipped. Nothing has moved yet.";
        CurrentView = AppView.RestorePreview;
        Announce($"Preview for profile '{profile.Name}': {actionable} actionable item(s), {ambiguous} need review, {skips} skipped. No window has moved.");
        RefreshCommandGuards();
    }

    private void PreviewRow_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewItemRow.IsSelected) or nameof(PreviewItemRow.ResolvedOption))
        {
            OnPropertyChanged(nameof(SelectedPreviewCount));
            applyCommand.RaiseCanExecuteChanged();
        }
    }

    public void RequestApply()
    {
        if (currentPlan is null)
            return;
        int count = SelectedPreviewCount;
        if (count == 0)
        {
            Announce("Select at least one preview item before applying.");
            return;
        }
        if (count > 1)
        {
            Confirmation = ConfirmationContext.Apply;
            ConfirmationText = $"Apply {count} planned window operations? Ambiguous items only apply after you pick an explicit match.";
            Announce(ConfirmationText);
            return;
        }
        _ = RunApplyAsync();
    }

    internal async Task RunApplyAsync()
    {
        if (currentPlan is null || previewedProfile is null)
            return;
        ErrorMessage = null;
        IReadOnlyList<PreviewItemRow> selected = PreviewRows.Where(row => row.IsSelected).ToArray();
        if (selected.Count == 0)
            return;

        ImmutableArray<RestorePlanItem>.Builder items = ImmutableArray.CreateBuilder<RestorePlanItem>();
        foreach (PreviewItemRow row in selected)
        {
            RestorePlanItem original = currentPlan.Items.First(item => item.SavedWindowId == row.SavedWindowId);
            if (original.Action is RestoreAction.Ambiguous)
            {
                RestorePlanItem? resolved = BuildResolvedItem(original, row);
                if (resolved is not null)
                    items.Add(resolved);
            }
            else
            {
                items.Add(original with { IsIncluded = true });
            }
        }

        if (items.Count == 0)
            return;
        RestorePlan chosen = new(currentPlan.PlanId, items.ToImmutable(), currentPlan.UndoSnapshot);

        using CancellationTokenSource applyCancellation = new();
        this.applyCancellation = applyCancellation;
        Progress.IsBusy = true;
        Progress.Label = $"Applying {items.Count} operation(s). Use Cancel to stop before the next window.";
        Progress.Maximum = items.Count;
        Progress.Value = 0;
        Announce(Progress.Label);
        RefreshCommandGuards();

        try
        {
            coordinator ??= new RestoreCoordinator(OpenWindowSystem());
            UndoReceipt receipt = await coordinator.ApplyAsync(chosen, applyCancellation.Token).ConfigureAwait(false);
            lastReceipt = receipt;
            ResultRows.Clear();
            Dictionary<string, string> appBySaved = previewedProfile.Windows.ToDictionary(
                w => w.WindowId,
                w => w.Title is { Length: > 0 } t ? $"{w.Application.ApplicationId} — {t}" : w.Application.ApplicationId,
                StringComparer.Ordinal);
            foreach (WindowOutcome outcome in receipt.Outcomes)
                ResultRows.Add(new OutcomeRow(outcome.SavedWindowId, appBySaved.GetValueOrDefault(outcome.SavedWindowId, outcome.SavedWindowId), CodeLabel(outcome.Code), outcome.Message));

            int succeeded = receipt.Outcomes.Count(o => o.Code is WindowOutcomeCode.Succeeded);
            int cancelled = receipt.Outcomes.Count(o => o.Code is WindowOutcomeCode.Cancelled);
            int failed = receipt.Outcomes.Count(o => o.Code is WindowOutcomeCode.Failed or WindowOutcomeCode.PermissionDenied or WindowOutcomeCode.Unsupported);
            ResultSummary = cancelled > 0
                ? $"Restore cancelled. {succeeded} completed, {failed} failed, {cancelled} cancelled."
                : $"Restore finished: {succeeded} succeeded, {failed} not completed.";
            UndoAvailable = receipt.Outcomes.Any(o => o.Code is WindowOutcomeCode.Succeeded or WindowOutcomeCode.Failed or WindowOutcomeCode.PermissionDenied);
            CurrentView = AppView.ApplyResult;
            Announce(ResultSummary + (UndoAvailable ? " Undo is available on this screen." : " Nothing was applied, so no undo is needed."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Announce($"Apply failed: {exception.Message}");
        }
        finally
        {
            this.applyCancellation = null;
            Progress.IsBusy = false;
            Progress.Label = string.Empty;
            RefreshCommandGuards();
        }
    }

    private void CancelApply()
    {
        applyCancellation?.Cancel();
        Announce("Cancellation requested. Windows not yet applied will be reported as cancelled.");
    }

    public async Task UndoAsync(CancellationToken cancellationToken = default)
    {
        if (coordinator is null || lastReceipt is null || !UndoAvailable)
            return;
        ErrorMessage = null;
        Progress.IsBusy = true;
        Progress.Label = "Restoring windows to their immediately pre-apply positions.";
        RefreshCommandGuards();
        try
        {
            ImmutableArray<WindowOutcome> outcomes = await coordinator.UndoAsync(lastReceipt, cancellationToken).ConfigureAwait(false);
            lastUndoOutcomes = outcomes;
            ResultRows.Clear();
            foreach (WindowOutcome outcome in outcomes)
                ResultRows.Add(new OutcomeRow(outcome.SavedWindowId, outcome.CurrentWindowId ?? outcome.SavedWindowId, CodeLabel(outcome.Code), outcome.Message));
            int succeeded = outcomes.Count(o => o.Code is WindowOutcomeCode.Succeeded);
            ResultSummary = $"Undo complete: {succeeded} of {outcomes.Length} best-effort restorations succeeded.";
            UndoAvailable = false;
            lastReceipt = null;
            CurrentView = AppView.ApplyResult;
            Announce(ResultSummary);
        }
        catch (InvalidOperationException exception)
        {
            Announce(exception.Message);
        }
        finally
        {
            Progress.IsBusy = false;
            Progress.Label = string.Empty;
            RefreshCommandGuards();
        }
    }

    // -------- confirmation + navigation --------

    public async Task ConfirmAsync(CancellationToken cancellationToken = default)
    {
        switch (Confirmation)
        {
            case ConfirmationContext.Apply:
                Confirmation = ConfirmationContext.None;
                await RunApplyAsync().ConfigureAwait(false);
                break;
            case ConfirmationContext.Delete:
                Confirmation = ConfirmationContext.None;
                await DeleteSelectedProfileAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    public void Decline()
    {
        Confirmation = ConfirmationContext.None;
        Announce("Action cancelled. Nothing changed.");
    }

    public void GoHome()
    {
        Confirmation = ConfirmationContext.None;
        CurrentView = AppView.Home;
        RefreshCommandGuards();
    }

    public void ShowPermissionNotice(string detail)
    {
        PermissionTitle = "Desktop access is unavailable";
        PermissionDetail = detail;
        CurrentView = AppView.PermissionNotice;
        Announce($"{detail} You can return to profiles at any time.");
        RefreshCommandGuards();
    }

    public async Task RefreshPermissionBannerAsync(CancellationToken cancellationToken)
    {
        if (permissionProvider is null)
        {
            PermissionTitle = "Local only";
            PermissionDetail = OperatingSystem.IsWindows()
                ? "Window Recall runs as a standard user process and never requests administrator rights."
                : "Window Recall never requests Screen Recording or administrator rights.";
            PermissionCanOpenSettings = false;
            return;
        }

        try
        {
            PermissionBanner banner = await permissionProvider.GetBannerAsync(cancellationToken).ConfigureAwait(false);
            PermissionTitle = banner.Title;
            PermissionDetail = banner.Detail;
            PermissionCanOpenSettings = banner.CanOpenSettings;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PermissionTitle = "Permission status unavailable";
            PermissionDetail = exception.Message;
            PermissionCanOpenSettings = false;
        }
    }

    public async Task OpenPermissionSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (permissionProvider is null)
            return;
        bool opened = await permissionProvider.OpenSystemSettingsAsync(cancellationToken).ConfigureAwait(false);
        Announce(opened ? "System settings opened." : "System settings could not be opened automatically.");
    }

    // -------- helpers --------

    private IWindowSystem OpenWindowSystem()
    {
        windowSystem ??= windowSystemFactory();
        return windowSystem;
    }

    private Task<CurrentDesktop> CaptureDesktopAsync(CancellationToken cancellationToken) =>
        OpenWindowSystem().CaptureAsync(cancellationToken);

    private void Announce(string message) => StatusMessage = message;

    private async Task<string> NextProfileIdAsync(string name, CancellationToken cancellationToken)
    {
        string slug = Slugify(name);
        ImmutableArray<string> existing = await profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(slug))
            return slug;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{slug}-{suffix}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    internal static string Slugify(string name)
    {
        string normalized = name.Trim().ToLowerInvariant();
        char[] chars = normalized
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        string collapsed = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
            collapsed = "profile";
        if (collapsed.Length > 48)
            collapsed = collapsed[..48].TrimEnd('-');
        return collapsed;
    }

    internal static ImmutableArray<string> ParsePatterns(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToImmutableArray();

    private bool TryValidatePatterns(ImmutableArray<string> patterns)
    {
        foreach (string pattern in patterns)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.None, RedactionProbeTimeout);
            }
            catch (ArgumentException exception)
            {
                Announce($"'{pattern}' is not a valid redaction pattern: {exception.Message}");
                return false;
            }
        }

        return true;
    }

    private RestorePlanItem? BuildResolvedItem(RestorePlanItem ambiguousItem, PreviewItemRow row)
    {
        if (previewedProfile is null || currentPlan is null || row.ResolvedOption?.CurrentWindowId is null)
        {
            Announce($"'{row.ApplicationLabel}' still needs an explicit match before it can be applied.");
            return null;
        }
        WindowSnapshot saved = previewedProfile.Windows.First(w => w.WindowId == ambiguousItem.SavedWindowId);
        DisplaySnapshot? source = saved.DisplayId is null
            ? previewedProfile.Displays.FirstOrDefault(d => d.IsPrimary) ?? previewedProfile.Displays.FirstOrDefault()
            : previewedProfile.Displays.FirstOrDefault(d => d.Id == saved.DisplayId);
        if (source is null)
            return null;
        DisplaySnapshot? target = null;
        foreach (DisplayMapping mapping in topologyMapper.MapDetailedAsync(previewedProfile.Displays, currentPlan.UndoSnapshot.Displays, CancellationToken.None)
                     .GetAwaiter().GetResult())
        {
            if (mapping.SavedDisplayId == source.Id)
            {
                target = currentPlan.UndoSnapshot.Displays.FirstOrDefault(d => d.Id == mapping.CurrentDisplayId);
                break;
            }
        }

        if (target is null)
        {
            Announce($"No current display is available to resolve '{row.ApplicationLabel}'.");
            return null;
        }

        DesktopRect bounds = RestoreGeometry.ConvertNormalized(saved.NormalBounds, source.WorkArea, target.WorkArea);
        return new RestorePlanItem(
            ambiguousItem.SavedWindowId,
            row.ResolvedOption.CurrentWindowId,
            RestoreAction.MoveResize,
            bounds,
            saved.State,
            $"Manually resolved ambiguous match to '{row.ResolvedOption.Label}'. Bounds were normalized and clamped on-screen.",
            true);
    }

    private static ImmutableArray<AmbiguityOption> BuildCandidates(WindowMatch? match, CurrentDesktop desktop)
    {
        if (match is null || match.Status is not WindowMatchStatus.Ambiguous)
            return ImmutableArray<AmbiguityOption>.Empty;
        Dictionary<string, WindowSnapshot> currentById = desktop.Windows.ToDictionary(w => w.WindowId, StringComparer.Ordinal);
        return match.Candidates
            .Where(candidate => candidate.IsAmbiguous)
            .Select(candidate => currentById.TryGetValue(candidate.CurrentWindowId, out WindowSnapshot? w)
                ? new AmbiguityOption(candidate.CurrentWindowId, $"{w.Application.ApplicationId} ({candidate.CurrentWindowId}), confidence {candidate.Confidence:P0}")
                : new AmbiguityOption(candidate.CurrentWindowId, candidate.CurrentWindowId))
            .ToImmutableArray();
    }

    private static string ActionLabel(RestoreAction action) => action switch
    {
        RestoreAction.MoveResize => "Move and resize",
        RestoreAction.ChangeState => "Change state",
        RestoreAction.Launch => "Launch (explicit opt-in)",
        RestoreAction.Skip => "Skip",
        RestoreAction.Ambiguous => "Needs review",
        _ => action.ToString(),
    };

    private static string CodeLabel(WindowOutcomeCode code) => code switch
    {
        WindowOutcomeCode.Succeeded => "OK",
        WindowOutcomeCode.Skipped => "SKIPPED",
        WindowOutcomeCode.Cancelled => "CANCELLED",
        WindowOutcomeCode.NotFound => "NOT FOUND",
        WindowOutcomeCode.Ambiguous => "NEEDS REVIEW",
        WindowOutcomeCode.PermissionDenied => "PERMISSION",
        WindowOutcomeCode.Unsupported => "UNSUPPORTED",
        WindowOutcomeCode.Failed => "FAILED",
        _ => code.ToString(),
    };

    private static string DescribeDisplay(DisplaySnapshot display) =>
        $"{display.Name ?? display.Id}{(display.IsPrimary ? " (primary)" : string.Empty)} · {display.Bounds.Width.ToString("F0", CultureInfo.InvariantCulture)}×{display.Bounds.Height.ToString("F0", CultureInfo.InvariantCulture)}";

    private static string FormatBounds(DesktopRect bounds) =>
        $"x={bounds.X.ToString("F0", CultureInfo.InvariantCulture)} y={bounds.Y.ToString("F0", CultureInfo.InvariantCulture)} w={bounds.Width.ToString("F0", CultureInfo.InvariantCulture)} h={bounds.Height.ToString("F0", CultureInfo.InvariantCulture)}";

    private static LaunchPolicyRow Map(LaunchPolicy policy) => policy switch
    {
        LaunchPolicy.AllowExplicitLaunch => LaunchPolicyRow.AllowExplicitLaunch,
        _ => LaunchPolicyRow.Never,
    };

    private static LaunchPolicy Unmap(LaunchPolicyRow row) => row switch
    {
        LaunchPolicyRow.AllowExplicitLaunch => LaunchPolicy.AllowExplicitLaunch,
        _ => LaunchPolicy.Never,
    };
}
