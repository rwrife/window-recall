using System.Collections.Immutable;

namespace WindowRecall.Core;

/// <summary>Maps displays using stable hints first and deterministic physical/topological evidence second.</summary>
public sealed class DisplayTopologyMapper : IDisplayMapper
{
    private readonly StringComparer identifierComparer = StringComparer.Ordinal;

    public async Task<ImmutableDictionary<string, string>> MapAsync(
        ImmutableArray<DisplaySnapshot> saved,
        ImmutableArray<DisplaySnapshot> current,
        CancellationToken cancellationToken = default) =>
        (await MapDetailedAsync(saved, current, cancellationToken).ConfigureAwait(false))
            .ToImmutableDictionary(mapping => mapping.SavedDisplayId, mapping => mapping.CurrentDisplayId, identifierComparer);

    public Task<ImmutableArray<DisplayMapping>> MapDetailedAsync(
        ImmutableArray<DisplaySnapshot> saved,
        ImmutableArray<DisplaySnapshot> current,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (current.IsEmpty)
            return Task.FromResult(ImmutableArray<DisplayMapping>.Empty);

        DisplaySnapshot? savedPrimary = saved.FirstOrDefault(display => display.IsPrimary);
        DisplaySnapshot currentPrimary = current.FirstOrDefault(display => display.IsPrimary) ??
            current.OrderBy(display => display.Id, StringComparer.Ordinal).First();
        Pair[] pairs = (from source in saved
                        from target in current
                        select Score(source, target, savedPrimary, currentPrimary))
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Saved.Id, identifierComparer)
            .ThenBy(pair => pair.Current.Id, identifierComparer)
            .ToArray();

        HashSet<string> assignedSaved = new(StringComparer.Ordinal);
        HashSet<string> assignedCurrent = new(StringComparer.Ordinal);
        Dictionary<string, DisplayMapping> mappings = new(StringComparer.Ordinal);
        foreach (Pair pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!assignedSaved.Add(pair.Saved.Id) || !assignedCurrent.Add(pair.Current.Id))
            {
                assignedSaved.RemoveWhere(id => id == pair.Saved.Id && mappings.ContainsKey(id) is false);
                continue;
            }
            mappings.Add(pair.Saved.Id, new(pair.Saved.Id, pair.Current.Id, pair.Kind, pair.Score, pair.Reason));
        }

        foreach (DisplaySnapshot source in saved.Where(display => !mappings.ContainsKey(display.Id)))
        {
            mappings.Add(source.Id, new(source.Id, currentPrimary.Id, DisplayMappingKind.PrimaryFallback, 0,
                "Saved display is missing from the current topology; using the current primary display as a recoverable fallback."));
        }

        return Task.FromResult(mappings.Values.OrderBy(mapping => mapping.SavedDisplayId, identifierComparer).ToImmutableArray());
    }

    private static Pair Score(DisplaySnapshot saved, DisplaySnapshot current, DisplaySnapshot? savedPrimary, DisplaySnapshot currentPrimary)
    {
        bool id = StringComparer.OrdinalIgnoreCase.Equals(saved.Id, current.Id);
        bool name = saved.Name is not null && current.Name is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(saved.Name, current.Name);
        double score = id ? 10_000 : name ? 5_000 : 0;
        List<string> evidence = [];
        if (id) evidence.Add("stable platform id");
        if (name) evidence.Add("stable display name");
        if (saved.IsPrimary == current.IsPrimary) { score += 400; evidence.Add("primary status"); }
        if (saved.Orientation == current.Orientation) { score += 300; evidence.Add("orientation"); }
        double savedAspect = saved.WorkArea.Width / saved.WorkArea.Height;
        double currentAspect = current.WorkArea.Width / current.WorkArea.Height;
        score += 200 / (1 + Math.Abs(Math.Log(savedAspect / currentAspect)));
        score += 100 / (1 + Math.Abs(Math.Log(saved.ScaleFactor / current.ScaleFactor)));
        score += 100 / (1 + Math.Abs(Math.Log((saved.WorkArea.Width * saved.WorkArea.Height) /
                                               (current.WorkArea.Width * current.WorkArea.Height))));
        evidence.Add("work-area size, scale, and aspect");
        if (savedPrimary is not null)
        {
            (int X, int Y) savedPosition = Relative(saved, savedPrimary);
            (int X, int Y) currentPosition = Relative(current, currentPrimary);
            if (savedPosition == currentPosition) { score += 500; evidence.Add("relative topology"); }
        }
        DisplayMappingKind kind = id || name ? DisplayMappingKind.StableHint : DisplayMappingKind.Topology;
        return new(saved, current, score, kind, $"Mapped using {string.Join(", ", evidence)}.");
    }

    private static (int X, int Y) Relative(DisplaySnapshot display, DisplaySnapshot primary)
    {
        double x = display.WorkArea.X + display.WorkArea.Width / 2 - (primary.WorkArea.X + primary.WorkArea.Width / 2);
        double y = display.WorkArea.Y + display.WorkArea.Height / 2 - (primary.WorkArea.Y + primary.WorkArea.Height / 2);
        return (Math.Sign(x), Math.Sign(y));
    }

    private sealed record Pair(DisplaySnapshot Saved, DisplaySnapshot Current, double Score, DisplayMappingKind Kind, string Reason);
}
