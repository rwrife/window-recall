using System.Collections.Immutable;

namespace WindowRecall.Core;

/// <summary>Ranks platform-neutral identity and structural hints without guessing through ties.</summary>
public sealed class DeterministicWindowMatcher : IWindowMatcher
{
    public Task<ImmutableArray<MatchCandidate>> MatchAsync(
        LayoutProfile profile,
        CurrentDesktop currentDesktop,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(currentDesktop);
        cancellationToken.ThrowIfCancellationRequested();

        ImmutableArray<MatchCandidate>.Builder all = ImmutableArray.CreateBuilder<MatchCandidate>();
        foreach (WindowSnapshot saved in profile.Windows.OrderBy(window => window.WindowId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CandidateScore[] scores = currentDesktop.Windows
                .Select(current => Score(saved, current))
                .Where(score => score.Score >= 50)
                .OrderByDescending(score => score.Score)
                .ThenBy(score => score.Current.WindowId, StringComparer.Ordinal)
                .ToArray();
            double best = scores.FirstOrDefault()?.Score ?? double.NaN;
            bool ambiguous = scores.Count(score => score.Score == best) > 1;
            for (int index = 0; index < scores.Length; index++)
            {
                CandidateScore score = scores[index];
                all.Add(new MatchCandidate(saved.WindowId, score.Current.WindowId, score.Score / 220d,
                    score.Evidence, index + 1, score.Tier, ambiguous && score.Score == best));
            }
        }
        return Task.FromResult(all.ToImmutable());
    }

    /// <summary>Produces a stable one-to-one resolution; ties remain explicit and unassigned.</summary>
    public async Task<ImmutableArray<WindowMatch>> ResolveAsync(
        LayoutProfile profile,
        CurrentDesktop currentDesktop,
        CancellationToken cancellationToken = default)
    {
        ImmutableArray<MatchCandidate> ranked = await MatchAsync(profile, currentDesktop, cancellationToken).ConfigureAwait(false);
        HashSet<string> used = new(StringComparer.Ordinal);
        ImmutableArray<WindowMatch>.Builder results = ImmutableArray.CreateBuilder<WindowMatch>();
        foreach (WindowSnapshot saved in profile.Windows.OrderBy(window => window.WindowId, StringComparer.Ordinal))
        {
            ImmutableArray<MatchCandidate> available = ranked
                .Where(candidate => candidate.SavedWindowId == saved.WindowId && !used.Contains(candidate.CurrentWindowId))
                .Select((candidate, index) => candidate with { Rank = index + 1 })
                .ToImmutableArray();
            if (available.IsEmpty)
            {
                results.Add(new(saved.WindowId, null, WindowMatchStatus.Unmatched, available,
                    "No current window has sufficient application identity evidence."));
                continue;
            }

            double best = available[0].Confidence;
            MatchCandidate[] tied = available.Where(candidate => candidate.Confidence == best).ToArray();
            if (tied.Length > 1)
            {
                ImmutableArray<MatchCandidate> ambiguous = available
                    .Select(candidate => candidate with { IsAmbiguous = candidate.Confidence == best }).ToImmutableArray();
                results.Add(new(saved.WindowId, null, WindowMatchStatus.Ambiguous, ambiguous,
                    $"{tied.Length} current windows share the best evidence; explicit resolution is required."));
                continue;
            }

            ImmutableArray<MatchCandidate> resolved = available.Select(candidate => candidate with { IsAmbiguous = false }).ToImmutableArray();
            used.Add(resolved[0].CurrentWindowId);
            results.Add(new(saved.WindowId, resolved[0].CurrentWindowId, WindowMatchStatus.Matched, resolved,
                $"Selected rank 1 using {resolved[0].Tier} evidence."));
        }
        return results.ToImmutable();
    }

    private static CandidateScore Score(WindowSnapshot saved, WindowSnapshot current)
    {
        double score = 0;
        MatchEvidenceTier tier = MatchEvidenceTier.None;
        ImmutableArray<string>.Builder evidence = ImmutableArray.CreateBuilder<string>();
        if (!Equal(saved.Application.ApplicationId, current.Application.ApplicationId))
            return new(current, 0, tier, evidence.ToImmutable());
        if (StringComparer.Ordinal.Equals(saved.WindowId, current.WindowId))
        {
            // Exact opaque identity must dominate every possible combination of weaker
            // path, role, and title hints (whose bonuses sum to 70).
            score += 100;
            tier = MatchEvidenceTier.ExactSessionIdentity;
            evidence.Add("opaque session identity exact");
        }
        score += 50;
        tier = Max(tier, MatchEvidenceTier.StableApplicationIdentity);
        evidence.Add("application identity exact");
        if (saved.Application.ExecutablePath is not null &&
            Equal(saved.Application.ExecutablePath, current.Application.ExecutablePath))
        {
            score += 40;
            tier = Max(tier, MatchEvidenceTier.StableApplicationIdentity);
            evidence.Add("executable identity exact");
        }
        if (saved.Role is not null && Equal(saved.Role, current.Role))
        {
            score += 20;
            tier = Max(tier, MatchEvidenceTier.ApplicationAndStructure);
            evidence.Add("window role exact");
        }
        if (saved.Title is not null && Equal(Normalize(saved.Title), Normalize(current.Title)))
        {
            score += 10;
            tier = Max(tier, MatchEvidenceTier.TitleHint);
            evidence.Add("optional normalized title hint exact");
        }
        return new(current, score, tier, evidence.ToImmutable());
    }

    private static MatchEvidenceTier Max(MatchEvidenceTier left, MatchEvidenceTier right) => left > right ? left : right;
    private static bool Equal(string? left, string? right) =>
        left is not null && right is not null && StringComparer.OrdinalIgnoreCase.Equals(left, right);
    private static string? Normalize(string? value) => value is null ? null : string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));

    private sealed record CandidateScore(WindowSnapshot Current, double Score, MatchEvidenceTier Tier, ImmutableArray<string> Evidence);
}
