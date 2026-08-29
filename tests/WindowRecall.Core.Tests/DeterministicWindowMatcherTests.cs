using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

public sealed class DeterministicWindowMatcherTests
{
    [Fact]
    public async Task RanksStableIdentityBeforeRoleAndTitleEvidence()
    {
        LayoutProfile profile = Profile(
            Saved("saved", "app", "/same", "document", "Quarterly report"));
        CurrentDesktop current = Desktop(
            Saved("weak", "app", "/other", "document", "Quarterly report"),
            Saved("strong", "app", "/same", "other", "Other"));

        ImmutableArray<MatchCandidate> candidates = await new DeterministicWindowMatcher().MatchAsync(profile, current);

        Assert.Collection(candidates,
            first =>
            {
                Assert.Equal("strong", first.CurrentWindowId);
                Assert.Equal(1, first.Rank);
                Assert.Equal(MatchEvidenceTier.StableApplicationIdentity, first.Tier);
                Assert.False(first.IsAmbiguous);
            },
            second => Assert.Equal("weak", second.CurrentWindowId));
    }

    [Fact]
    public async Task ExactSessionIdentityOutranksAllWeakerCombinedHints()
    {
        LayoutProfile profile = Profile(
            Saved("same-session", "app", "/saved", "document", "Quarterly report"));
        CurrentDesktop current = Desktop(
            Saved("same-session", "app", "/changed", "other", "Other"),
            Saved("all-weaker-hints", "app", "/saved", "document", "Quarterly report"));

        ImmutableArray<MatchCandidate> candidates = await new DeterministicWindowMatcher().MatchAsync(profile, current);

        Assert.Equal("same-session", candidates[0].CurrentWindowId);
        Assert.Equal(MatchEvidenceTier.ExactSessionIdentity, candidates[0].Tier);
    }

    [Fact]
    public async Task EqualBestCandidatesAreExplicitlyAmbiguousAndNeverSilentlyResolved()
    {
        LayoutProfile profile = Profile(Saved("saved", "app", null, "document", "Same"));
        CurrentDesktop current = Desktop(
            Saved("b", "app", null, "document", "Same"),
            Saved("a", "app", null, "document", "Same"));

        ImmutableArray<MatchCandidate> candidates = await new DeterministicWindowMatcher().MatchAsync(profile, current);

        Assert.Equal(["a", "b"], candidates.Select(candidate => candidate.CurrentWindowId));
        Assert.All(candidates, candidate => Assert.True(candidate.IsAmbiguous));
        Assert.Equal([1, 2], candidates.Select(candidate => candidate.Rank));
    }

    [Fact]
    public async Task MultipleSavedWindowsUseDeterministicOneToOneAssignment()
    {
        LayoutProfile profile = Profile(
            Saved("saved-b", "app", null, "beta", null),
            Saved("saved-a", "app", null, "alpha", null));
        CurrentDesktop current = Desktop(
            Saved("current-2", "app", null, "beta", null),
            Saved("current-1", "app", null, "alpha", null));

        ImmutableArray<WindowMatch> matches = await new DeterministicWindowMatcher().ResolveAsync(profile, current);

        Assert.Equal(["saved-a", "saved-b"], matches.Select(match => match.SavedWindowId));
        Assert.Equal(["current-1", "current-2"], matches.Select(match => match.CurrentWindowId));
        Assert.All(matches, match => Assert.Equal(WindowMatchStatus.Matched, match.Status));
    }

    [Fact]
    public async Task TitleOnlyEvidenceDoesNotProduceAConfidentMatch()
    {
        LayoutProfile profile = Profile(Saved("saved", "saved-app", null, null, "Shared title"));
        CurrentDesktop current = Desktop(Saved("current", "different-app", null, null, "Shared title"));

        WindowMatch match = Assert.Single(await new DeterministicWindowMatcher().ResolveAsync(profile, current));

        Assert.Equal(WindowMatchStatus.Unmatched, match.Status);
        Assert.Null(match.CurrentWindowId);
    }

    [Fact]
    public async Task ReusedOpaqueSessionIdCannotOverrideApplicationIdentity()
    {
        LayoutProfile profile = Profile(Saved("same-session", "saved-app", null, null, null));
        CurrentDesktop current = Desktop(Saved("same-session", "different-app", null, null, null));

        WindowMatch match = Assert.Single(await new DeterministicWindowMatcher().ResolveAsync(profile, current));

        Assert.Equal(WindowMatchStatus.Unmatched, match.Status);
    }

    [Fact]
    public async Task SeededPermutationPropertyProducesIdenticalResolution()
    {
        WindowSnapshot[] saved = Enumerable.Range(0, 20)
            .Select(index => Saved($"saved-{index:D2}", $"app-{index % 5}", $"/app/{index}", $"role-{index}", $"title-{index}"))
            .ToArray();
        WindowSnapshot[] current = saved.Select((window, index) => window with { WindowId = $"current-{index:D2}" }).ToArray();
        DeterministicWindowMatcher matcher = new();
        string expected = Serialize(await matcher.ResolveAsync(Profile(saved), Desktop(current)));
        Random random = new(8675309);

        for (int iteration = 0; iteration < 50; iteration++)
        {
            WindowSnapshot[] shuffledSaved = saved.OrderBy(_ => random.Next()).ToArray();
            WindowSnapshot[] shuffledCurrent = current.OrderBy(_ => random.Next()).ToArray();
            Assert.Equal(expected, Serialize(await matcher.ResolveAsync(Profile(shuffledSaved), Desktop(shuffledCurrent))));
        }
    }

    private static string Serialize(ImmutableArray<WindowMatch> matches) =>
        string.Join("|", matches.Select(match => $"{match.SavedWindowId}:{match.CurrentWindowId}:{match.Status}"));

    private static LayoutProfile Profile(params WindowSnapshot[] windows) => new(
        1, "profile", DateTimeOffset.UnixEpoch, [], windows.ToImmutableArray(), new(false));

    private static CurrentDesktop Desktop(params WindowSnapshot[] windows) =>
        new(DateTimeOffset.UnixEpoch, [], windows.ToImmutableArray());

    private static WindowSnapshot Saved(string id, string app, string? path, string? role, string? title) =>
        new(id, new(app, path), role, title, new(0, 0, 100, 100), WindowState.Normal, null);
}
