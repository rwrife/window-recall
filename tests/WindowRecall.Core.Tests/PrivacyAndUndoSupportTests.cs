using System.Collections.Immutable;
using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

/// <summary>Coverage for the CLI-supporting Core additions: title redaction, undo-receipt
/// persistence, shell-identity rejection, and the shared undo execution mechanics.</summary>
public sealed class PrivacyAndUndoSupportTests
{
    [Fact]
    public void Serialize_AppliesRedactionPatternsToPersistedTitles()
    {
        LayoutProfile profile = ProfileFixture.Create(persistTitles: true, title: "Invoice 90210 draft") with
        {
            Privacy = new ProfilePrivacy(true) { RedactionPatterns = ["\\d{4,}"] },
        };

        string json = ProfileJsonSerializer.Serialize(profile);

        Assert.Contains("Invoice [redacted] draft", json, StringComparison.Ordinal);
        Assert.DoesNotContain("90210", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RedactionIsNotAppliedWhenTitlesAreNotPersisted()
    {
        LayoutProfile profile = ProfileFixture.Create(persistTitles: false, title: "secret") with
        {
            Privacy = new ProfilePrivacy(false) { RedactionPatterns = ["secret"] },
        };

        string json = ProfileJsonSerializer.Serialize(profile);

        // The title field disappears entirely; the pattern list is metadata, not a persisted title.
        Assert.DoesNotContain("\"title\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsMalformedOrInvalidRedactionPatterns()
    {
        Assert.Throws<ProfileFormatException>(() => ProfileValidator.Validate(
            ProfileFixture.Create(persistTitles: true) with
            {
                Privacy = new ProfilePrivacy(true) { RedactionPatterns = ["("] },
            }));
        Assert.Throws<ProfileFormatException>(() => ProfileValidator.Validate(
            ProfileFixture.Create(persistTitles: true) with
            {
                Privacy = new ProfilePrivacy(true) { RedactionPatterns = ["   "] },
            }));
        Assert.Throws<ProfileFormatException>(() => ProfileValidator.Validate(
            ProfileFixture.Create(persistTitles: true) with
            {
                Privacy = new ProfilePrivacy(true) { RedactionPatterns = [new string('a', 513)] },
            }));
    }

    [Fact]
    public void TitleRedactor_TimeoutFailsClosed()
    {
        // A nested-quantifier pattern is catastrophic; the timeout must yield the marker, never the title.
        string? result = TitleRedactor.Redact(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!",
            ["(a+)+$"]);
        Assert.Equal(TitleRedactor.Marker, result);
    }

    [Fact]
    public void RedactionPatterns_RoundTripThroughSerialization()
    {
        LayoutProfile profile = ProfileFixture.Create(persistTitles: true) with
        {
            Privacy = new ProfilePrivacy(true) { RedactionPatterns = ["\\d+", "secret"] },
        };

        LayoutProfile restored = ProfileJsonSerializer.Deserialize(ProfileJsonSerializer.Serialize(profile));

        Assert.Equal(["\\d+", "secret"], restored.Privacy.RedactionPatterns.ToArray());
    }

    [Fact]
    public void UndoReceipt_SerializationStripsTitlesAndRoundTrips()
    {
        CurrentDesktop before = new(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            [new DisplaySnapshot("d", null, new DesktopRect(0, 0, 100, 100), new DesktopRect(0, 0, 100, 100), 1, DisplayOrientation.Landscape, true)],
            [new WindowSnapshot("cur-1", new ApplicationIdentity("com.example"), null, "Private title", new DesktopRect(1, 2, 3, 4), WindowState.Normal, "d")]);
        UndoReceipt receipt = new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, before,
            [new WindowOutcome("saved-1", "cur-1", WindowOutcomeCode.Succeeded, "ok")]);

        string json = UndoReceiptJsonSerializer.Serialize(receipt);
        Assert.DoesNotContain("Private title", json, StringComparison.Ordinal);

        UndoReceipt restored = UndoReceiptJsonSerializer.Deserialize(json);
        Assert.Equal(receipt.ReceiptId, restored.ReceiptId);
        Assert.Equal(receipt.PlanId, restored.PlanId);
        Assert.Null(restored.BeforeRestore.Windows[0].Title);
        Assert.Equal(new DesktopRect(1, 2, 3, 4), restored.BeforeRestore.Windows[0].NormalBounds);
        Assert.Single(restored.Outcomes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ nope")]
    [InlineData("[]")]
    public void UndoReceipt_MalformedDocuments_FailClosed(string json) =>
        Assert.Throws<ProfileFormatException>(() => UndoReceiptJsonSerializer.Deserialize(json));

    [Theory]
    [InlineData(null, false)]
    [InlineData("/Applications/Editor.app/Contents/MacOS/Editor", false)]
    [InlineData(@"C:\Program Files (x86)\Editor\editor.exe", false)]
    [InlineData("/bin/sh -c \"evil\"", true)]
    [InlineData("~/scripts/editor", true)]
    [InlineData("editor && curl evil.example", true)]
    [InlineData("editor;rm -rf /", true)]
    [InlineData("editor|tee log", true)]
    [InlineData("$(whoami)", true)]
    [InlineData("editor`id`", true)]
    [InlineData("relative/path/editor", true)]
    [InlineData("editor*", true)]
    [InlineData("/bin/sh -c id", true)]
    [InlineData("/usr/bin/pwsh -Command Get-Process", true)]
    [InlineData("/bin/sh", false)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", false)]
    [InlineData(@"/Applications/My App.app/Contents/MacOS/MyApp", false)]
    public void ExecutableIdentity_RejectsShellShapedPaths(string? executablePath, bool expectUnsafe)
    {
        LayoutProfile profile = ProfileFixture.Create() with
        {
            Windows = [ProfileFixture.Create().Windows[0] with
            {
                Application = new ApplicationIdentity("com.example.editor", executablePath),
            }],
        };

        ImmutableArray<string> unsafeIdentities = ExecutableIdentityValidator.FindUnsafeIdentities(profile);

        Assert.Equal(expectUnsafe, !unsafeIdentities.IsEmpty);
    }

    [Fact]
    public async Task UndoExecution_ReplaysPreApplyGeometryThroughTheAdapter()
    {
        CurrentDesktop before = new(
            DateTimeOffset.UtcNow,
            [new DisplaySnapshot("d", null, new DesktopRect(0, 0, 100, 100), new DesktopRect(0, 0, 100, 100), 1, DisplayOrientation.Landscape, true)],
            [new WindowSnapshot("cur-1", new ApplicationIdentity("com.example"), null, null, new DesktopRect(10, 10, 30, 30), WindowState.Normal, "d")]);
        UndoReceipt receipt = new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, before,
            [
                new WindowOutcome("saved-1", "cur-1", WindowOutcomeCode.Succeeded, "applied"),
                new WindowOutcome("saved-2", "cur-2", WindowOutcomeCode.NotFound, "never applied, so never replayed"),
            ]);
        InMemoryWindowSystem system = new(before with
        {
            Windows = [before.Windows[0] with { NormalBounds = new DesktopRect(99, 99, 30, 30) }],
        });

        ImmutableArray<WindowOutcome> outcomes = await UndoExecution.RunAsync(system, receipt);

        Assert.Single(outcomes);
        Assert.Equal(WindowOutcomeCode.Succeeded, outcomes[0].Code);
        Assert.Equal("saved-1", outcomes[0].SavedWindowId);
        Assert.Equal(new DesktopRect(10, 10, 30, 30), Observe(system).Windows.Single().NormalBounds);
    }

    [Fact]
    public async Task CoordinatorUndo_UsesSharedExecutionAndKeepsOneStepRule()
    {
        CurrentDesktop before = new(
            DateTimeOffset.UtcNow,
            [new DisplaySnapshot("d", null, new DesktopRect(0, 0, 100, 100), new DesktopRect(0, 0, 100, 100), 1, DisplayOrientation.Landscape, true)],
            [new WindowSnapshot("current-1", new ApplicationIdentity("com.example.editor"), null, null, new DesktopRect(5, 5, 30, 30), WindowState.Normal, "d")]);
        InMemoryWindowSystem system = new(before);
        RestoreCoordinator coordinator = new(system);
        RestorePlan plan = new(Guid.NewGuid(),
            [new RestorePlanItem("saved-1", "current-1", RestoreAction.MoveResize, new DesktopRect(60, 60, 30, 30), WindowState.Normal, "test", true)],
            before);

        UndoReceipt receipt = await coordinator.ApplyAsync(plan);
        ImmutableArray<WindowOutcome> undo = await coordinator.UndoAsync(receipt);

        Assert.Single(undo);
        Assert.Equal(WindowOutcomeCode.Succeeded, undo[0].Code);
        Assert.Equal(new DesktopRect(5, 5, 30, 30), Observe(system).Windows.Single().NormalBounds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.UndoAsync(receipt));
    }


    private static CurrentDesktop Observe(InMemoryWindowSystem system) =>
        system.CaptureAsync().GetAwaiter().GetResult();
}
