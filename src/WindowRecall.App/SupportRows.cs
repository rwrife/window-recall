using System.Collections.Immutable;

namespace WindowRecall.App;

/// <summary>Per-window toggle row used by profile review and the editor. The stored title shown here
/// is whatever the profile carried (redaction happens at save time in Core); when title storage is
/// off it is null and rows identify windows by application id only.</summary>
public sealed class WindowToggleItem : ViewModelBase
{
    private bool isExcluded;
    private LaunchPolicyRow launchPolicy;

    public WindowToggleItem(string savedWindowId, string applicationId, string? title, string displayLabel)
    {
        if (string.IsNullOrWhiteSpace(savedWindowId))
            throw new ArgumentException("Saved window id is required.", nameof(savedWindowId));
        SavedWindowId = savedWindowId;
        ApplicationId = applicationId ?? string.Empty;
        Title = title;
        DisplayLabel = displayLabel;
        launchPolicy = LaunchPolicyRow.Never;
    }

    public string SavedWindowId { get; }
    public string ApplicationId { get; }

    /// <summary>Persisted (redacted) title, or null when the profile does not store titles.</summary>
    public string? Title { get; }

    /// <summary>Human label that never depends on the title: application id plus saved display.</summary>
    public string DisplayLabel { get; }

    /// <summary>Inclusion choice: excluded windows are dropped before the profile is saved.</summary>
    public bool IsExcluded
    {
        get => isExcluded;
        set => SetProperty(ref isExcluded, value);
    }

    public LaunchPolicyRow LaunchPolicy
    {
        get => launchPolicy;
        set => SetProperty(ref launchPolicy, value);
    }

    /// <summary>Row label combining glyph, identity, and display so status never relies on color.</summary>
    public string DisplayLine => $"{(IsExcluded ? "[-] " : "[+] ")}{ApplicationId}{(Title is { Length: > 0 } t ? $" — {t}" : string.Empty)} · {DisplayLabel} · launch: {LaunchPolicy}";
}

/// <summary>UI-facing mirror of <see cref="Core.LaunchPolicy"/> (Core types stay out of XAML bindings).</summary>
public enum LaunchPolicyRow
{
    Never,
    AllowExplicitLaunch,
}

/// <summary>One resolved row of the restore preview.</summary>
public sealed class PreviewItemRow : ViewModelBase
{
    private bool isSelected;
    private AmbiguityOption? resolvedOption;

    public PreviewItemRow(
        string savedWindowId,
        string applicationLabel,
        string actionLabel,
        string sourceDisplay,
        string destinationDisplay,
        string targetBoundsSummary,
        string reason,
        bool isSelected,
        bool canSelect,
        string statusGlyph,
        string stateAnnouncement)
    {
        SavedWindowId = savedWindowId;
        ApplicationLabel = applicationLabel;
        ActionLabel = actionLabel;
        SourceDisplay = sourceDisplay;
        DestinationDisplay = destinationDisplay;
        TargetBoundsSummary = targetBoundsSummary;
        Reason = reason;
        this.isSelected = isSelected;
        CanSelect = canSelect;
        StatusGlyph = statusGlyph;
        StateAnnouncement = stateAnnouncement;
        CandidateOptions = ImmutableArray<AmbiguityOption>.Empty;
    }

    public string SavedWindowId { get; }
    public string ApplicationLabel { get; }

    /// <summary>Plain-language action: Move and resize, Change state, Launch, Skip, or Needs review.</summary>
    public string ActionLabel { get; }

    /// <summary>Which monitor the layout expects the window to come from.</summary>
    public string SourceDisplay { get; }

    public string DestinationDisplay { get; }
    public string TargetBoundsSummary { get; }
    public string Reason { get; }

    /// <summary>Text glyph cue so status never relies on color alone.</summary>
    public string StatusGlyph { get; }

    /// <summary>Screen-reader sentence describing the row state.</summary>
    public string StateAnnouncement { get; }

    /// <summary>Choices shown when this row is ambiguous; empty otherwise.</summary>
    public ImmutableArray<AmbiguityOption> CandidateOptions { get; init; }

    /// <summary>The user-chosen resolution for an ambiguous row; applying requires one.</summary>
    public AmbiguityOption? ResolvedOption
    {
        get => resolvedOption;
        set
        {
            if (SetProperty(ref resolvedOption, value) && value is not null && !CanSelect)
                AllowSelection();
        }
    }

    private bool canSelect;

    public bool CanSelect
    {
        get => canSelect;
        private set => SetProperty(ref canSelect, value);
    }

    /// <summary>Unlocks selection for a row that was initially locked (ambiguous until resolved).</summary>
    public void AllowSelection() => CanSelect = true;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (value && !CanSelect)
                return;
            SetProperty(ref isSelected, value);
        }
    }
}

/// <summary>One entry of the per-window result list after apply or undo.</summary>
public sealed class OutcomeRow
{
    public OutcomeRow(string savedWindowId, string applicationLabel, string codeLabel, string message)
    {
        SavedWindowId = savedWindowId;
        ApplicationLabel = applicationLabel;
        CodeLabel = codeLabel;
        Message = message;
    }

    public string SavedWindowId { get; }
    public string ApplicationLabel { get; }
    public string CodeLabel { get; }
    public string Message { get; }

    /// <summary>Text-glyph row rendering so status is readable without color.</summary>
    public string Display => $"[{CodeLabel}] {ApplicationLabel} — {Message}";
}

/// <summary>Summarizes one currently attached display for the topology panel.</summary>
public sealed record CurrentTopologyRow(string Id, string Label, bool IsPrimary);
