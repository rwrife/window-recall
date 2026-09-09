using System.Runtime.InteropServices;

namespace WindowRecall.Cli;

/// <summary>Resolves the documented per-OS local storage layout shared by the CLI and the app.</summary>
public static class CliPaths
{
    public const string DataRootEnvironmentVariable = "WINDOW_RECALL_DATA_ROOT";

    /// <summary>Precedence: explicit --data-root, then WINDOW_RECALL_DATA_ROOT, then the per-OS default:
    /// Windows %LOCALAPPDATA%\window-recall, Linux/macOS XDG local state (~/.local/share/window-recall,
    /// which is what LocalApplicationData maps to on non-Windows). The value is a directory root, never a
    /// shell-expanded path: tilde is not interpreted.</summary>
    public static string ResolveDataRoot(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);
        string? fromEnvironment = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);
        string local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return Path.Combine(local, "window-recall");
    }

    public static string ProfilesDirectory(string dataRoot) => Path.Combine(dataRoot, "profiles");

    public static string UndoReceiptPath(string dataRoot) => Path.Combine(dataRoot, "undo", "latest-undo.json");

    public static string PlatformName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" : "other";
}
