using System.Collections.Immutable;

namespace WindowRecall.Core;

/// <summary>Builds deterministic, immutable, non-mutating restore previews.</summary>
public sealed class RestorePlanner : IRestorePlanner
{
    private readonly DeterministicWindowMatcher matcher;
    private readonly DisplayTopologyMapper displayMapper;

    public RestorePlanner() : this(new DeterministicWindowMatcher(), new DisplayTopologyMapper()) { }

    public RestorePlanner(DeterministicWindowMatcher matcher, DisplayTopologyMapper displayMapper)
    {
        this.matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        this.displayMapper = displayMapper ?? throw new ArgumentNullException(nameof(displayMapper));
    }

    public async Task<RestorePlan> CreatePlanAsync(
        LayoutProfile profile,
        CurrentDesktop currentDesktop,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(currentDesktop);
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<WindowMatch> matches = await matcher.ResolveAsync(profile, currentDesktop, cancellationToken).ConfigureAwait(false);
        ImmutableArray<DisplayMapping> mappings = await displayMapper.MapDetailedAsync(profile.Displays, currentDesktop.Displays, cancellationToken).ConfigureAwait(false);
        Dictionary<string, WindowMatch> matchById = matches.ToDictionary(match => match.SavedWindowId, StringComparer.Ordinal);
        Dictionary<string, DisplayMapping> mappingById = mappings.ToDictionary(mapping => mapping.SavedDisplayId, StringComparer.Ordinal);
        Dictionary<string, DisplaySnapshot> savedDisplays = profile.Displays.ToDictionary(display => display.Id, StringComparer.Ordinal);
        Dictionary<string, DisplaySnapshot> currentDisplays = currentDesktop.Displays.ToDictionary(display => display.Id, StringComparer.Ordinal);
        DisplaySnapshot? defaultSavedDisplay = profile.Displays.FirstOrDefault(display => display.IsPrimary) ?? profile.Displays.FirstOrDefault();

        ImmutableArray<RestorePlanItem>.Builder items = ImmutableArray.CreateBuilder<RestorePlanItem>();
        foreach (WindowSnapshot saved in profile.Windows.OrderBy(window => window.WindowId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowMatch match = matchById[saved.WindowId];
            if (match.Status is WindowMatchStatus.Ambiguous)
            {
                items.Add(new(saved.WindowId, null, RestoreAction.Ambiguous, null, null,
                    $"{match.Reason} Ambiguous matches are never moved automatically.", false));
                continue;
            }
            if (match.Status is WindowMatchStatus.Unmatched)
            {
                bool launch = saved.LaunchPolicy is LaunchPolicy.AllowExplicitLaunch;
                items.Add(new(saved.WindowId, null, launch ? RestoreAction.Launch : RestoreAction.Skip, null, null,
                    launch
                        ? "Application is not running; launch is available only after explicit launch approval."
                        : "Application is not running and this profile disallows launch.", false));
                continue;
            }

            DisplaySnapshot? source = saved.DisplayId is not null && savedDisplays.TryGetValue(saved.DisplayId, out DisplaySnapshot? specified)
                ? specified : defaultSavedDisplay;
            if (source is null || !mappingById.TryGetValue(source.Id, out DisplayMapping? mapping) ||
                !currentDisplays.TryGetValue(mapping.CurrentDisplayId, out DisplaySnapshot? target))
            {
                items.Add(new(saved.WindowId, match.CurrentWindowId, RestoreAction.Skip, null, null,
                    "No usable current display is available for safe bounds conversion.", false));
                continue;
            }

            DesktopRect bounds;
            try
            {
                bounds = RestoreGeometry.ConvertNormalized(saved.NormalBounds, source.WorkArea, target.WorkArea);
            }
            catch (ArgumentException exception)
            {
                items.Add(new(saved.WindowId, match.CurrentWindowId, RestoreAction.Skip, null, null,
                    $"Saved geometry is invalid and was skipped: {exception.Message}", false));
                continue;
            }
            items.Add(new(saved.WindowId, match.CurrentWindowId, RestoreAction.MoveResize, bounds, saved.State,
                $"Matched deterministically. {mapping.Reason} Bounds were normalized and clamped to a recoverable on-screen region.", true));
        }

        return new RestorePlan(Guid.NewGuid(), items.ToImmutable(), currentDesktop);
    }
}
