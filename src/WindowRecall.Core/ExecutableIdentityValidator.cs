using System.Collections.Immutable;

namespace WindowRecall.Core;

/// <summary>Rejects stored application identities that could only be honored by building a shell command
/// string. Window Recall never launches through a shell, so any executable path that embeds shell control
/// syntax, tilde expansion, or a relative/command-line shape is treated as hostile profile data.</summary>
public static class ExecutableIdentityValidator
{
    /// <summary>Characters that give a path shell meaning on Windows, macOS, or POSIX shells. Parentheses
    /// and spaces are allowed because they occur inside legitimate absolute paths such as
    /// "C:\Program Files (x86)\...".</summary>
    private static readonly char[] ShellControlCharacters =
    [
        '<', '>', '|', '&', ';', '`', '$', '*', '?', '"', '\'', '{', '}', '[', ']', '!',
    ];

    private static readonly HashSet<string> ShellInterpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "dash", "zsh", "ksh", "csh", "tcsh", "fish",
        "cmd", "powershell", "pwsh", "wsh", "cscript", "wscript",
    };

    /// <summary>Returns per-window reasons why a profile stores executable identities that cannot be used
    /// without shell-string execution, or an empty result when every identity is safe.</summary>
    public static ImmutableArray<string> FindUnsafeIdentities(LayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ImmutableArray<string>.Builder reasons = ImmutableArray.CreateBuilder<string>();
        foreach (WindowSnapshot window in profile.Windows)
        {
            if (window.Application.ApplicationId.Any(character => character < ' '))
                reasons.Add($"Window '{window.WindowId}': application id contains control characters.");
            string? path = window.Application.ExecutablePath;
            if (path is null)
                continue;
            if (path.Any(character => character < ' ' && character != '\0') || path.Contains('\0'))
                reasons.Add($"Window '{window.WindowId}': executable path contains control characters.");
            else if (path.IndexOfAny(ShellControlCharacters) >= 0 || path.Contains("$(", StringComparison.Ordinal) || path.Contains("${", StringComparison.Ordinal))
                reasons.Add($"Window '{window.WindowId}': executable path contains shell control syntax.");
            else if (path.StartsWith('~'))
                reasons.Add($"Window '{window.WindowId}': executable path uses shell home expansion.");
            else if (!IsAbsolutePath(path))
                reasons.Add($"Window '{window.WindowId}': executable path must be an absolute path, not a relative path or command line.");
            else if (IsShellInterpreter(path))
                reasons.Add($"Window '{window.WindowId}': executable identity names a shell interpreter, which can only be used by building a shell command string.");
        }
        return reasons.ToImmutable();
    }

    private static bool IsAbsolutePath(string path) =>
        path.StartsWith('/') ||
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));

    private static bool IsShellInterpreter(string path)
    {
        // A plain identity like "C:\...\powershell.exe" or "/bin/sh" is just a window owner's path
        // and stays valid. A *command-line shape* whose first token names a shell interpreter
        // (for example "/bin/sh -c id") could only ever be honored by shell-string execution,
        // because no real path contains those embedded argument tokens.
        if (!path.Contains(' ') && !path.Contains('\t'))
            return false;
        string fileName = Path.GetFileName(path.TrimEnd(path.Contains('\\') ? '\\' : '/'));
        string firstToken = fileName.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return ShellInterpreters.Contains(Path.GetFileNameWithoutExtension(firstToken));
    }
}
