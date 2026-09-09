using System.Text.RegularExpressions;

namespace WindowRecall.Core;

/// <summary>Applies profile-stored regex redaction patterns to raw window titles before persistence.
/// Patterns run with a bounded timeout; a pattern that times out fails closed by replacing the whole
/// title with the marker, so a pathological pattern can never leak the title it was meant to hide.</summary>
public static class TitleRedactor
{
    public const string Marker = "[redacted]";

    public static string? Redact(string? title, IReadOnlyList<string> patterns)
    {
        if (title is null || patterns is null || patterns.Count == 0)
            return title;
        string result = title;
        foreach (string pattern in patterns)
        {
            try
            {
                result = Regex.Replace(result, pattern, Marker, RegexOptions.None, ProfileValidator.RedactionTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                return Marker;
            }
            catch (ArgumentException)
            {
                // Validator rejects invalid patterns before serialization; defense in depth fails closed.
                return Marker;
            }
        }
        return result;
    }
}
