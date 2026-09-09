using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowRecall.Core;

namespace WindowRecall.Cli;

/// <summary>Stable machine-readable envelope for <c>--json</c> output. Status and exit-code pairs are
/// documented in docs/cli.md and must not change shape without an explicit version decision.</summary>
internal sealed record CliEnvelope(
    [property: JsonPropertyOrder(0)] string Command,
    [property: JsonPropertyOrder(1)] string Status,
    [property: JsonPropertyOrder(2)] int ExitCode,
    [property: JsonPropertyOrder(3)] string? Message,
    [property: JsonPropertyOrder(4)] object? Data);

internal sealed record PlanItemData(
    [property: JsonPropertyOrder(0)] string SavedWindowId,
    [property: JsonPropertyOrder(1)] string? CurrentWindowId,
    [property: JsonPropertyOrder(2)] RestoreAction Action,
    [property: JsonPropertyOrder(3)] DesktopRect? TargetBounds,
    [property: JsonPropertyOrder(4)] WindowState? TargetState,
    [property: JsonPropertyOrder(5)] string Reason,
    [property: JsonPropertyOrder(6)] bool IsIncluded,
    [property: JsonPropertyOrder(7)] bool CanAutoApply);

internal sealed record PlanData(
    [property: JsonPropertyOrder(0)] string ProfileId,
    [property: JsonPropertyOrder(1)] Guid PlanId,
    [property: JsonPropertyOrder(2)] ImmutableArray<PlanItemData> Items);

internal sealed record OutcomeData(
    [property: JsonPropertyOrder(0)] string SavedWindowId,
    [property: JsonPropertyOrder(1)] string? CurrentWindowId,
    [property: JsonPropertyOrder(2)] WindowOutcomeCode Code,
    [property: JsonPropertyOrder(3)] string Message);

internal sealed record ApplyData(
    [property: JsonPropertyOrder(0)] bool DryRun,
    [property: JsonPropertyOrder(1)] PlanData Plan,
    [property: JsonPropertyOrder(2)] ImmutableArray<OutcomeData> Outcomes,
    [property: JsonPropertyOrder(3)] string? UndoReceiptPath);

internal sealed record CaptureData(
    [property: JsonPropertyOrder(0)] string ProfileId,
    [property: JsonPropertyOrder(1)] string Name,
    [property: JsonPropertyOrder(2)] DateTimeOffset CapturedAtUtc,
    [property: JsonPropertyOrder(3)] int DisplayCount,
    [property: JsonPropertyOrder(4)] int WindowCount,
    [property: JsonPropertyOrder(5)] bool PersistWindowTitles,
    [property: JsonPropertyOrder(6)] ImmutableArray<string> RedactionPatterns);

internal sealed record ProfilesListData(ImmutableArray<string> Profiles);

internal sealed record ProfileReport(
    [property: JsonPropertyOrder(0)] string ProfileId,
    [property: JsonPropertyOrder(1)] bool Valid,
    [property: JsonPropertyOrder(2)] int? SchemaVersion,
    [property: JsonPropertyOrder(3)] bool? PersistWindowTitles,
    [property: JsonPropertyOrder(4)] int? RedactionPatternCount,
    [property: JsonPropertyOrder(5)] int? StoredTitleCount,
    [property: JsonPropertyOrder(6)] int? WindowCount,
    [property: JsonPropertyOrder(7)] ImmutableArray<string>? UnsafeExecutableIdentities,
    [property: JsonPropertyOrder(8)] string? Error);

internal sealed record ImportExportData(string ProfileId, string? Path, bool Overwrote, string? ProfileDocument);

internal sealed record UndoData(
    Guid ReceiptId,
    ImmutableArray<OutcomeData> Outcomes,
    string ConsumedPath);

internal sealed record PrivacyBaseline(string NetworkUsage, string TitleStorage, int TotalStoredTitles);

internal sealed record DoctorData(
    string Platform,
    string DataRoot,
    bool ProfilesDirectoryExists,
    WindowSystemCapabilities Adapter,
    ImmutableArray<ProfileReport> Profiles,
    ImmutableArray<string> Issues,
    PrivacyBaseline Privacy);

/// <summary>Headless automation surface built on the same Core services as the GUI. Apply is dry-run by
/// default, ambiguous items are never auto-applied, import never silently overwrites, and every failure
/// mode maps to one documented exit code (see docs/cli.md).</summary>
public sealed class CliRunner
{
    public const int ExitSuccess = 0;
    public const int ExitValidation = 2;
    public const int ExitNoMatch = 3;
    public const int ExitPermissionMissing = 4;
    public const int ExitPartial = 5;
    public const int ExitConflict = 6;

    public const string StatusSuccess = "success";
    public const string StatusPlanned = "planned";
    public const string StatusValidation = "validationError";
    public const string StatusNoMatch = "noMatch";
    public const string StatusPermissionMissing = "permissionMissing";
    public const string StatusPartial = "partial";
    public const string StatusConflict = "conflict";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string[] Usage =
    [
        "usage: window-recall <command> [options]",
        "",
        "commands:",
        "  capture --name <name> [--profile-id <id>] [--persist-titles] [--redact <regex>]... [--force]",
        "  profiles list | profiles inspect <profile-id>",
        "  plan <profile-id>",
        "  apply <profile-id> [--execute] [--select <saved-window-id>]... [--launch-missing]",
        "  undo",
        "  export <profile-id> [--out <file> | --stdout] [--force]",
        "  import <file> [--profile-id <id>] [--on-conflict fail|rename|overwrite]",
        "  doctor",
        "",
        "common options: --json for stable machine output, --data-root <path> to override local storage.",
        "apply is dry-run unless --execute. Ambiguous matches are never applied. Exit codes: docs/cli.md.",
    ];

    private readonly TextWriter stdout;
    private readonly TextWriter stderr;
    private readonly Func<IWindowSystem> windowSystemFactory;
    private bool json;

    public CliRunner(TextWriter stdout, TextWriter stderr, Func<IWindowSystem>? windowSystemFactory = null)
    {
        this.stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
        this.stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
        this.windowSystemFactory = windowSystemFactory ?? LiveDesktop.Open;
    }

    public async Task<int> RunAsync(string[] args)
    {
        json = args.Contains("--json", StringComparer.Ordinal);
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            foreach (string line in Usage)
                stdout.WriteLine(line);
            return args.Length == 0 ? ExitValidation : ExitSuccess;
        }

        string command = args[0];
        if (command is "--version" or "-v" or "version")
        {
            stdout.WriteLine("window-recall cli 0.1.0");
            return ExitSuccess;
        }

        try
        {
            return command switch
            {
                "capture" => await CaptureAsync(args).ConfigureAwait(false),
                "profiles" => await ProfilesAsync(args).ConfigureAwait(false),
                "plan" => await PlanAsync(args).ConfigureAwait(false),
                "apply" => await ApplyAsync(args).ConfigureAwait(false),
                "undo" => await UndoAsync(args).ConfigureAwait(false),
                "export" => await ExportAsync(args).ConfigureAwait(false),
                "import" => await ImportAsync(args).ConfigureAwait(false),
                "doctor" => await DoctorAsync(args).ConfigureAwait(false),
                _ => Fail(command, $"Unknown command '{command}'. Run 'window-recall --help' for usage."),
            };
        }
        catch (IOException exception)
        {
            return Fail(command, $"I/O failure: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Fail(command, $"Access denied: {exception.Message}");
        }
    }

    private async Task<int> CaptureAsync(string[] args)
    {
        ParseResult parsed = Parse(args, supportsSelect: false, supportsRedact: true);
        if (parsed.Error is not null) return Fail("capture", parsed.Error);
        Options options = parsed.Options!.Value;
        if (options.Name is null) return Fail("capture", "capture requires --name <name>.");
        if (!options.PersistTitles && options.Redact.Length > 0)
            return Fail("capture", "--redact patterns require --persist-titles because titles are removed when persistence is off.");

        string dataRoot = CliPaths.ResolveDataRoot(options.DataRoot);
        string profileId = options.ProfileId ?? Slugify(options.Name);
        if (string.IsNullOrEmpty(profileId)) return Fail("capture", "Could not derive a safe profile id; pass --profile-id explicitly.");

        JsonFileProfileStore store = new JsonFileProfileStore(CliPaths.ProfilesDirectory(dataRoot));
        if ((await store.ListAsync().ConfigureAwait(false)).Contains(profileId, StringComparer.Ordinal) && !options.Force)
            return Emit("capture", StatusConflict, ExitConflict, $"Profile '{profileId}' already exists; pass --force to replace it.", null);

        LayoutProfile profile;
        try
        {
            CurrentDesktop desktop = await windowSystemFactory().CaptureAsync(CancellationToken.None).ConfigureAwait(false);
            profile = new LayoutProfile(
                LayoutProfile.CurrentSchemaVersion,
                options.Name,
                desktop.ObservedAtUtc,
                desktop.Displays,
                desktop.Windows,
                new ProfilePrivacy(options.PersistTitles) { RedactionPatterns = options.Redact });
            ProfileValidator.Validate(profile);
        }
        catch (PlatformNotSupportedException exception)
        {
            return Emit("capture", StatusPermissionMissing, ExitPermissionMissing, exception.Message, null);
        }
        catch (ProfileFormatException exception)
        {
            return Fail("capture", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Fail("capture", exception.Message);
        }

        ImmutableArray<string> unsafeIdentities = ExecutableIdentityValidator.FindUnsafeIdentities(profile);
        if (!unsafeIdentities.IsEmpty)
            return Fail("capture", $"Refusing to store application identities that would need shell-string execution: {string.Join(" ", unsafeIdentities)}");

        await store.SaveAsync(profileId, profile, CancellationToken.None).ConfigureAwait(false);
        CaptureData data = new(profileId, options.Name, profile.CapturedAtUtc, profile.Displays.Length,
            profile.Windows.Length, profile.Privacy.PersistWindowTitles, profile.Privacy.RedactionPatterns);
        return Emit("capture", StatusSuccess, ExitSuccess, $"Saved profile '{profileId}'.", data);
    }

    private async Task<int> ProfilesAsync(string[] args)
    {
        if (args.Length < 2 || args[1] is not ("list" or "inspect"))
            return Fail("profiles", "profiles requires 'list' or 'inspect <profile-id>'.");
        string sub = args[1];
        if (sub == "inspect" && (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal)))
            return Fail("profiles inspect", "profiles inspect requires a profile id directly after 'inspect'.");
        string[] tail = sub == "inspect"
            ? args.Length > 3 ? args[3..] : []
            : args.Length > 2 ? args[2..] : [];
        ParseResult parsed = Parse(tail, supportsSelect: false, supportsRedact: false);
        if (parsed.Error is not null) return Fail("profiles", parsed.Error);
        string dataRoot = CliPaths.ResolveDataRoot(parsed.Options!.Value.DataRoot);
        JsonFileProfileStore store = new(CliPaths.ProfilesDirectory(dataRoot));

        if (sub == "list")
        {
            ImmutableArray<string> ids = await store.ListAsync().ConfigureAwait(false);
            return Emit("profiles list", StatusSuccess, ExitSuccess, null, new ProfilesListData(ids));
        }

        string profileId = args[2];
        ProfileReport report = await InspectProfileAsync(store, profileId).ConfigureAwait(false);
        return report.Valid
            ? Emit("profiles inspect", StatusSuccess, ExitSuccess, null, report)
            : Emit("profiles inspect", StatusValidation, ExitValidation, report.Error, report);
    }

    private async Task<int> PlanAsync(string[] args)
    {
        ParseResult parsed = Parse(args[1..], supportsSelect: false, supportsRedact: false);
        if (parsed.Error is not null) return Fail("plan", parsed.Error);
        if (parsed.Options!.Value.Positional is null) return Fail("plan", "plan requires a profile id.");
        return await RunPlanAsync("plan", parsed.Options!.Value.Positional, CliPaths.ResolveDataRoot(parsed.Options!.Value.DataRoot),
            execute: false, select: [], launchMissing: false).ConfigureAwait(false);
    }

    private async Task<int> ApplyAsync(string[] args)
    {
        ParseResult parsed = Parse(args[1..], supportsSelect: true, supportsRedact: false);
        if (parsed.Error is not null) return Fail("apply", parsed.Error);
        if (parsed.Options!.Value.Positional is null) return Fail("apply", "apply requires a profile id.");
        return await RunPlanAsync("apply", parsed.Options!.Value.Positional, CliPaths.ResolveDataRoot(parsed.Options!.Value.DataRoot),
            execute: parsed.Options!.Value.Execute, select: parsed.Options!.Value.Selects, launchMissing: parsed.Options!.Value.LaunchMissing).ConfigureAwait(false);
    }

    private async Task<int> RunPlanAsync(
        string command, string profileId, string dataRoot, bool execute,
        IReadOnlyList<string> select, bool launchMissing)
    {
        JsonFileProfileStore store = new JsonFileProfileStore(CliPaths.ProfilesDirectory(dataRoot));
        LayoutProfile profile;
        try
        {
            profile = await store.LoadAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ProfileFormatException or ArgumentException or IOException)
        {
            return Emit(command, StatusValidation, ExitValidation, exception.Message, null);
        }

        IWindowSystem windowSystem = windowSystemFactory();
        WindowSystemCapabilities capabilities = await windowSystem.GetCapabilitiesAsync().ConfigureAwait(false);
        CurrentDesktop desktop;
        try
        {
            desktop = await windowSystem.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException exception)
        {
            return Emit(command, StatusPermissionMissing, ExitPermissionMissing, exception.Message, null);
        }

        RestorePlan plan = await new RestorePlanner().CreatePlanAsync(profile, desktop, CancellationToken.None).ConfigureAwait(false);

        RestorePlan selected;
        if (select.Count > 0)
        {
            ImmutableArray<string>.Builder rejected = ImmutableArray.CreateBuilder<string>();
            foreach (string requested in select)
            {
                RestorePlanItem? item = plan.Items.FirstOrDefault(candidate => candidate.SavedWindowId == requested);
                if (item is null)
                    rejected.Add($"'{requested}' is not in this plan.");
                else if (item.Action is RestoreAction.Ambiguous)
                    rejected.Add($"'{requested}' is ambiguous; ambiguous matches are never applied. Resolve it interactively first.");
                else if (!item.CanAutoApply && !(item.Action is RestoreAction.Launch && launchMissing))
                    rejected.Add($"'{requested}' is not eligible for headless apply (launch items additionally require --launch-missing).");
            }
            if (rejected.Count > 0)
                return Emit(command, StatusValidation, ExitValidation, $"Selection rejected: {string.Join(" ", rejected)}", null);
            selected = plan.WithSelection(select, includeLaunches: launchMissing);
        }
        else
        {
            IEnumerable<string> defaultSelection = plan.Items.Where(item => item.CanAutoApply)
                .Select(item => item.SavedWindowId)
                .Union(launchMissing ? plan.Items.Where(item => item.Action is RestoreAction.Launch).Select(item => item.SavedWindowId) : []);
            selected = plan.WithSelection(defaultSelection, includeLaunches: launchMissing);
        }

        PlanData planData = new(profileId, plan.PlanId, selected.Items.Select(item => item.ToData()).ToImmutableArray());
        bool anythingRunnable = selected.Items.Any(item => item.IsIncluded);

        if (!execute)
        {
            if (!anythingRunnable)
                return Emit(command, StatusNoMatch, ExitNoMatch,
                    "No deterministic match can be applied on the current desktop.", planData);
            return Emit(command, StatusPlanned, ExitSuccess, "Dry-run preview; pass --execute to apply.", planData);
        }

        if (!capabilities.CanObserve || (!capabilities.CanMoveResize && !capabilities.CanChangeState))
            return Emit(command, StatusPermissionMissing, ExitPermissionMissing,
                capabilities.Limitation ?? "The desktop adapter cannot move windows.", planData);
        if (!anythingRunnable)
            return Emit(command, StatusNoMatch, ExitNoMatch, "No selected item is applicable.", planData);

        RestoreCoordinator coordinator = new(windowSystem);
        UndoReceipt receipt = await coordinator.ApplyAsync(selected, CancellationToken.None).ConfigureAwait(false);
        ImmutableArray<OutcomeData>.Builder outcomes = receipt.Outcomes.Select(outcome => outcome.ToData()).ToImmutableArray().ToBuilder();

        string receiptPath = CliPaths.UndoReceiptPath(dataRoot);
        string? savedReceiptPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            File.WriteAllText(receiptPath, UndoReceiptJsonSerializer.Serialize(receipt));
            savedReceiptPath = receiptPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            savedReceiptPath = null;
            outcomes.Add(new OutcomeData("*", null, WindowOutcomeCode.Failed,
                $"Undo receipt could not be persisted: {exception.Message}"));
        }

        ImmutableArray<OutcomeData> finalOutcomes = outcomes.ToImmutable();
        bool allClean = finalOutcomes.All(outcome => outcome.Code is WindowOutcomeCode.Succeeded or WindowOutcomeCode.Skipped);
        return allClean
            ? Emit(command, StatusSuccess, ExitSuccess, "Restore applied; the undo receipt is stored until the next apply or a successful undo.", new ApplyData(false, planData, finalOutcomes, savedReceiptPath))
            : Emit(command, StatusPartial, ExitPartial, "Restore finished with per-window failures; inspect outcomes and consider 'undo'.", new ApplyData(false, planData, finalOutcomes, savedReceiptPath));
    }

    private async Task<int> UndoAsync(string[] args)
    {
        ParseResult parsed = Parse(args[1..], supportsSelect: false, supportsRedact: false);
        if (parsed.Error is not null) return Fail("undo", parsed.Error);
        string dataRoot = CliPaths.ResolveDataRoot(parsed.Options!.Value.DataRoot);
        string receiptPath = CliPaths.UndoReceiptPath(dataRoot);
        if (!File.Exists(receiptPath))
            return Emit("undo", StatusValidation, ExitValidation, $"No stored undo receipt at '{receiptPath}'.", null);

        UndoReceipt receipt;
        try
        {
            receipt = UndoReceiptJsonSerializer.Deserialize(await File.ReadAllTextAsync(receiptPath).ConfigureAwait(false));
        }
        catch (ProfileFormatException exception)
        {
            return Emit("undo", StatusValidation, ExitValidation, $"Stored undo receipt is invalid: {exception.Message}", null);
        }

        IWindowSystem windowSystem = windowSystemFactory();
        WindowSystemCapabilities capabilities = await windowSystem.GetCapabilitiesAsync().ConfigureAwait(false);
        if (!capabilities.CanObserve || (!capabilities.CanMoveResize && !capabilities.CanChangeState))
            return Emit("undo", StatusPermissionMissing, ExitPermissionMissing,
                capabilities.Limitation ?? "The desktop adapter cannot move windows; the undo receipt was left untouched.", null);

        ImmutableArray<WindowOutcome> undoOutcomes = await UndoExecution.RunAsync(windowSystem, receipt, CancellationToken.None).ConfigureAwait(false);
        string consumedPath = Path.Combine(Path.GetDirectoryName(receiptPath)!, $"consumed-{receipt.ReceiptId:N}.json");
        File.Move(receiptPath, consumedPath, overwrite: true);

        ImmutableArray<OutcomeData> outcomes = undoOutcomes.Select(outcome => outcome.ToData()).ToImmutableArray();
        bool allClean = outcomes.All(outcome => outcome.Code is WindowOutcomeCode.Succeeded or WindowOutcomeCode.Skipped or WindowOutcomeCode.NotFound);
        return allClean
            ? Emit("undo", StatusSuccess, ExitSuccess, "Undo finished; the receipt is archived.", new UndoData(receipt.ReceiptId, outcomes, consumedPath))
            : Emit("undo", StatusPartial, ExitPartial, "Undo finished with per-window issues; inspect outcomes.", new UndoData(receipt.ReceiptId, outcomes, consumedPath));
    }

    private async Task<int> ExportAsync(string[] args)
    {
        ParseResult parsed = Parse(args[1..], supportsSelect: false, supportsRedact: false);
        if (parsed.Error is not null) return Fail("export", parsed.Error);
        if (parsed.Options!.Value.Positional is null) return Fail("export", "export requires a profile id.");
        if (parsed.Options!.Value.OutPath is not null && parsed.Options!.Value.ToStdout)
            return Fail("export", "--out and --stdout are mutually exclusive.");
        string profileId = parsed.Options!.Value.Positional;
        string dataRoot = CliPaths.ResolveDataRoot(parsed.Options!.Value.DataRoot);
        JsonFileProfileStore store = new JsonFileProfileStore(CliPaths.ProfilesDirectory(dataRoot));

        string jsonDocument;
        try
        {
            jsonDocument = ProfileJsonSerializer.Serialize(await store.LoadAsync(profileId, CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is ProfileFormatException or ArgumentException or IOException)
        {
            return Emit("export", StatusValidation, ExitValidation, exception.Message, null);
        }

        if (parsed.Options!.Value.ToStdout)
        {
            if (json)
                return Emit("export", StatusSuccess, ExitSuccess, null, new ImportExportData(profileId, null, false, jsonDocument));
            stdout.Write(jsonDocument);
            return ExitSuccess;
        }

        string destination = parsed.Options!.Value.OutPath is not null
            ? Path.GetFullPath(parsed.Options!.Value.OutPath)
            : Path.Combine(Directory.GetCurrentDirectory(), $"{profileId}.window-recall.json");
        bool existed = File.Exists(destination);
        if (existed && !parsed.Options!.Value.Force)
            return Emit("export", StatusConflict, ExitConflict, $"'{destination}' already exists; pass --force to replace it.", null);
        File.WriteAllText(destination, jsonDocument);
        return Emit("export", StatusSuccess, ExitSuccess, $"Exported '{profileId}' to '{destination}'.", new ImportExportData(profileId, destination, existed, null));
    }

    private async Task<int> ImportAsync(string[] args)
    {
        ParseResult parsed = Parse(args[1..], supportsSelect: false, supportsRedact: false);
        if (parsed.Error is not null) return Fail("import", parsed.Error);
        if (parsed.Options!.Value.Positional is null) return Fail("import", "import requires a file path.");
        string source = parsed.Options!.Value.Positional;
        string dataRoot = CliPaths.ResolveDataRoot(parsed.Options!.Value.DataRoot);

        if (!File.Exists(source))
            return Emit("import", StatusValidation, ExitValidation, $"File '{source}' does not exist.", null);

        LayoutProfile profile;
        try
        {
            profile = ProfileJsonSerializer.Deserialize(await File.ReadAllTextAsync(source).ConfigureAwait(false));
        }
        catch (ProfileFormatException exception)
        {
            return Emit("import", StatusValidation, ExitValidation, exception.Message, null);
        }

        ImmutableArray<string> unsafeIdentities = ExecutableIdentityValidator.FindUnsafeIdentities(profile);
        if (!unsafeIdentities.IsEmpty)
            return Emit("import", StatusValidation, ExitValidation, $"Import rejected: {string.Join(" ", unsafeIdentities)}", null);

        string profileId = parsed.Options!.Value.ProfileId
            ?? Path.GetFileNameWithoutExtension(source).Replace(".window-recall", string.Empty, StringComparison.Ordinal);
        JsonFileProfileStore store = new JsonFileProfileStore(CliPaths.ProfilesDirectory(dataRoot));
        ImmutableArray<string> existing = await store.ListAsync().ConfigureAwait(false);

        string target = profileId;
        bool overwrote = false;
        if (existing.Contains(target, StringComparer.Ordinal))
        {
            switch (parsed.Options!.Value.OnConflict)
            {
                case "fail":
                    return Emit("import", StatusConflict, ExitConflict,
                        $"Profile '{target}' already exists; choose --on-conflict rename|overwrite.", null);
                case "rename":
                    target = ChooseRename(profileId, existing);
                    break;
                case "overwrite":
                    overwrote = true;
                    break;
                default:
                    return Fail("import", "--on-conflict must be fail, rename, or overwrite.");
            }
        }

        try
        {
            // Schema validation already happened in Deserialize; SaveAsync enforces id safety and atomic write.
            await store.SaveAsync(target, profile, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Emit("import", StatusValidation, ExitValidation, exception.Message, null);
        }

        return Emit("import", StatusSuccess, ExitSuccess, $"Imported profile '{target}'.", new ImportExportData(target, Path.GetFullPath(source), overwrote, null));
    }

    private async Task<int> DoctorAsync(string[] args)
    {
        ParseResult parsed = Parse(args, supportsSelect: false, supportsRedact: false);
        if (parsed.Error is not null) return Fail("doctor", parsed.Error);
        string dataRoot = CliPaths.ResolveDataRoot(parsed.Options!.Value.DataRoot);
        string profilesDirectory = CliPaths.ProfilesDirectory(dataRoot);
        JsonFileProfileStore store = new JsonFileProfileStore(profilesDirectory);
        ImmutableArray<string> ids = await store.ListAsync().ConfigureAwait(false);
        ImmutableArray<ProfileReport>.Builder reports = ImmutableArray.CreateBuilder<ProfileReport>();
        ImmutableArray<string>.Builder issues = ImmutableArray.CreateBuilder<string>();
        int totalStoredTitles = 0;
        foreach (string id in ids)
        {
            ProfileReport report = await InspectProfileAsync(store, id).ConfigureAwait(false);
            reports.Add(report);
            if (!report.Valid)
            {
                issues.Add($"Profile '{id}': {report.Error}");
            }
            else
            {
                totalStoredTitles += report.StoredTitleCount ?? 0;
                foreach (string unsafeIdentity in report.UnsafeExecutableIdentities ?? [])
                    issues.Add($"Profile '{id}': {unsafeIdentity}");
            }
        }

        string receiptPath = CliPaths.UndoReceiptPath(dataRoot);
        if (File.Exists(receiptPath))
        {
            try
            {
                _ = UndoReceiptJsonSerializer.Deserialize(await File.ReadAllTextAsync(receiptPath).ConfigureAwait(false));
            }
            catch (ProfileFormatException exception)
            {
                issues.Add($"Undo receipt: {exception.Message}");
            }
        }

        IWindowSystem windowSystem = windowSystemFactory();
        WindowSystemCapabilities capabilities = await windowSystem.GetCapabilitiesAsync().ConfigureAwait(false);
        DoctorData data = new(
            CliPaths.PlatformName(),
            dataRoot,
            Directory.Exists(profilesDirectory),
            capabilities,
            reports.ToImmutable(),
            issues.ToImmutable(),
            new PrivacyBaseline("none", "opt-in per profile", totalStoredTitles));
        return issues.Count == 0
            ? Emit("doctor", StatusSuccess, ExitSuccess, "Local storage, profiles, receipt, and adapter state are healthy.", data)
            : Emit("doctor", StatusValidation, ExitValidation, "Doctor found issues; inspect the profiles and issues arrays.", data);
    }

    // CA1859 would demand the concrete JsonFileProfileStore; the IProfileStore boundary is intentional
    // because the CLI must consume the same Core contract as the GUI and remain testable with fakes.
#pragma warning disable CA1859
    private static async Task<ProfileReport> InspectProfileAsync(IProfileStore store, string profileId)
#pragma warning restore CA1859
    {
        try
        {
            LayoutProfile profile = await store.LoadAsync(profileId, CancellationToken.None).ConfigureAwait(false);
            return new ProfileReport(
                profileId,
                true,
                profile.SchemaVersion,
                profile.Privacy.PersistWindowTitles,
                profile.Privacy.RedactionPatterns.Length,
                profile.Windows.Count(window => window.Title is not null),
                profile.Windows.Length,
                ExecutableIdentityValidator.FindUnsafeIdentities(profile),
                null);
        }
        catch (Exception exception) when (exception is ProfileFormatException or ArgumentException or IOException)
        {
            return new ProfileReport(profileId, false, null, null, null, null, null, null, exception.Message);
        }
    }

    private static string ChooseRename(string baseId, ImmutableArray<string> existing)
    {
        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string candidate = $"{baseId}-{suffix}";
            if (!existing.Contains(candidate, StringComparer.Ordinal))
                return candidate;
        }
        throw new IOException($"Could not choose a non-conflicting name for '{baseId}'.");
    }

    private static string Slugify(string name)
    {
        char[] lowered = name.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        string slug = new string(lowered).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('.', ' ');
    }

    private int Fail(string command, string message) =>
        Emit(command, StatusValidation, ExitValidation, message, null);

    private int Emit(string command, string status, int exitCode, string? message, object? data)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new CliEnvelope(command, status, exitCode, message, data), JsonOptions));
            return exitCode;
        }

        if (message is not null)
        {
            if (exitCode == ExitSuccess || status == StatusPlanned)
                stdout.WriteLine(message);
            else
                stderr.WriteLine(message);
        }

        switch (data)
        {
            case ProfilesListData list:
                foreach (string profile in list.Profiles)
                    stdout.WriteLine($"  {profile}");
                break;
            case PlanData plan:
                foreach (PlanItemData item in plan.Items)
                    stdout.WriteLine($"  {item.SavedWindowId}: {item.Action}{(item.CurrentWindowId is null ? "" : $" -> {item.CurrentWindowId}")}{(item.IsIncluded ? " [selected]" : "")} ({item.Reason})");
                break;
            case ApplyData apply:
                foreach (OutcomeData outcome in apply.Outcomes)
                    stdout.WriteLine($"  {outcome.SavedWindowId}: {outcome.Code} ({outcome.Message})");
                break;
            case UndoData undo:
                foreach (OutcomeData outcome in undo.Outcomes)
                    stdout.WriteLine($"  {outcome.SavedWindowId}: {outcome.Code} ({outcome.Message})");
                break;
            case DoctorData doctor:
                stdout.WriteLine($"  platform: {doctor.Platform}");
                stdout.WriteLine($"  data-root: {doctor.DataRoot}");
                stdout.WriteLine($"  adapter: observe={doctor.Adapter.CanObserve} move={doctor.Adapter.CanMoveResize} limitation={doctor.Adapter.Limitation ?? "none"}");
                foreach (string issue in doctor.Issues)
                    stdout.WriteLine($"  issue: {issue}");
                break;
        }
        return exitCode;
    }

    private readonly record struct Options(
        string? Positional,
        string? Name,
        string? ProfileId,
        string? DataRoot,
        string? OutPath,
        string OnConflict,
        bool PersistTitles,
        bool Force,
        bool ToStdout,
        bool Execute,
        bool LaunchMissing,
        ImmutableArray<string> Selects,
        ImmutableArray<string> Redact);

    private sealed record ParseResult(Options? Options, string? Error);

    private static ParseResult Parse(string[] tokens, bool supportsSelect, bool supportsRedact)
    {
        string? positional = null;
        int positionalCount = 0;
        string? name = null, profileId = null, dataRoot = null, outPath = null;
        string onConflict = "fail";
        bool persistTitles = false, force = false, toStdout = false, execute = false, launchMissing = false;
        List<string> selects = [];
        List<string> redact = [];

        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];

            // An option value is the next token unless it looks like another option.
            bool Next(out string value)
            {
                if (index + 1 < tokens.Length && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = tokens[++index];
                    return true;
                }
                value = string.Empty;
                return false;
            }

            switch (token)
            {
                case "--json":
                    break; // Consumed globally in RunAsync.
                case "--data-root":
                    if (!Next(out dataRoot!)) return new ParseResult(null, "Missing value for --data-root.");
                    break;
                case "--name":
                    if (!Next(out name!)) return new ParseResult(null, "Missing value for --name.");
                    break;
                case "--profile-id":
                    if (!Next(out profileId!)) return new ParseResult(null, "Missing value for --profile-id.");
                    break;
                case "--out":
                    if (!Next(out outPath!)) return new ParseResult(null, "Missing value for --out.");
                    break;
                case "--on-conflict":
                    if (!Next(out onConflict)) return new ParseResult(null, "Missing value for --on-conflict.");
                    break;
                case "--persist-titles":
                    persistTitles = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--stdout":
                    toStdout = true;
                    break;
                case "--execute":
                    execute = true;
                    break;
                case "--launch-missing":
                    launchMissing = true;
                    break;
                case "--select" when supportsSelect:
                    if (!Next(out string selectValue)) return new ParseResult(null, "Missing value for --select.");
                    selects.Add(selectValue);
                    break;
                case "--redact" when supportsRedact:
                    if (!Next(out string redactValue)) return new ParseResult(null, "Missing value for --redact.");
                    redact.Add(redactValue);
                    break;
                default:
                    if (token.StartsWith("--", StringComparison.Ordinal))
                        return new ParseResult(null, $"Unknown option '{token}'.");
                    positionalCount++;
                    positional = token;
                    break;
            }
        }

        if (positionalCount > 1)
            return new ParseResult(null, "Unexpected extra positional argument.");

        Options options = new(positional, name, profileId, dataRoot, outPath, onConflict,
            persistTitles, force, toStdout, execute, launchMissing,
            selects.ToImmutableArray(), redact.ToImmutableArray());
        return new ParseResult(options, null);
    }
}

internal static class CliMappers
{
    public static PlanItemData ToData(this RestorePlanItem item) =>
        new(item.SavedWindowId, item.CurrentWindowId, item.Action, item.TargetBounds, item.TargetState,
            item.Reason, item.IsIncluded, item.CanAutoApply);

    public static OutcomeData ToData(this WindowOutcome outcome) =>
        new(outcome.SavedWindowId, outcome.CurrentWindowId, outcome.Code, outcome.Message);
}
