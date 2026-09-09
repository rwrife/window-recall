using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowRecall.Core;

/// <summary>Base exception for profile data that cannot be safely consumed.</summary>
public class ProfileFormatException : Exception
{
    public ProfileFormatException(string message) : base(message) { }
    public ProfileFormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Signals a profile created by a newer, unsupported schema.</summary>
public sealed class UnsupportedProfileVersionException : ProfileFormatException
{
    public UnsupportedProfileVersionException(int version)
        : base($"Profile schema version {version} is newer than supported version {LayoutProfile.CurrentSchemaVersion}.") => Version = version;

    public int Version { get; }
}

/// <summary>Validates schema-v1 profiles.</summary>
public static class ProfileValidator
{
    public static void Validate(LayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SchemaVersion > LayoutProfile.CurrentSchemaVersion) throw new UnsupportedProfileVersionException(profile.SchemaVersion);
        if (profile.SchemaVersion != LayoutProfile.CurrentSchemaVersion) throw new ProfileFormatException($"Unsupported profile schema version {profile.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ProfileFormatException("Profile name is required.");
        if (profile.Displays.IsDefault || profile.Windows.IsDefault) throw new ProfileFormatException("Display and window collections are required.");
        if (profile.Privacy is null) throw new ProfileFormatException("Profile privacy settings are required.");
        if (profile.Displays.Any(display => display is null || string.IsNullOrWhiteSpace(display.Id) || display.ScaleFactor <= 0 || display.Bounds is null || display.WorkArea is null || !ValidRect(display.Bounds) || !ValidRect(display.WorkArea)))
            throw new ProfileFormatException("Every display requires an id, positive scale factor, and valid bounds.");
        if (profile.Displays.Select(display => display.Id).Distinct(StringComparer.Ordinal).Count() != profile.Displays.Length)
            throw new ProfileFormatException("Display ids must be unique.");
        if (profile.Windows.Any(window => window is null || string.IsNullOrWhiteSpace(window.WindowId) || window.Application is null || string.IsNullOrWhiteSpace(window.Application.ApplicationId) || window.NormalBounds is null || !ValidRect(window.NormalBounds)))
            throw new ProfileFormatException("Every window requires ids and valid positive bounds.");
        if (profile.Windows.Select(window => window.WindowId).Distinct(StringComparer.Ordinal).Count() != profile.Windows.Length)
            throw new ProfileFormatException("Window ids must be unique.");
        if (profile.Privacy.RedactionPatterns.Length > MaxRedactionPatterns)
            throw new ProfileFormatException($"At most {MaxRedactionPatterns} redaction patterns are allowed.");
        foreach (string pattern in profile.Privacy.RedactionPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxRedactionPatternLength || pattern.Any(character => character < ' '))
                throw new ProfileFormatException("Redaction patterns must be short printable strings.");
            try
            {
                _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, RedactionTimeout);
            }
            catch (ArgumentException exception)
            {
                throw new ProfileFormatException($"Redaction pattern '{pattern}' is not a valid regular expression.", exception);
            }
        }
    }

    private const int MaxRedactionPatterns = 32;
    private const int MaxRedactionPatternLength = 512;
    internal static readonly TimeSpan RedactionTimeout = TimeSpan.FromMilliseconds(250);

    private static bool ValidRect(DesktopRect rectangle) =>
        double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) && rectangle.Width > 0 && rectangle.Height > 0;
}

/// <summary>Reads and writes stable, human-readable profile schema v1 JSON.</summary>
public static class ProfileJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(LayoutProfile profile)
    {
        ProfileValidator.Validate(profile);
        LayoutProfile safeProfile;
        if (!profile.Privacy.PersistWindowTitles)
        {
            safeProfile = profile with { Windows = profile.Windows.Select(window => window with { Title = null }).ToImmutableArray() };
        }
        else if (profile.Privacy.RedactionPatterns.Length > 0)
        {
            safeProfile = profile with
            {
                Windows = profile.Windows.Select(window => window with { Title = TitleRedactor.Redact(window.Title, profile.Privacy.RedactionPatterns) }).ToImmutableArray(),
            };
        }
        else
        {
            safeProfile = profile;
        }
        return JsonSerializer.Serialize(safeProfile, Options) + Environment.NewLine;
    }

    public static LayoutProfile Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ProfileFormatException("Profile JSON is required.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProfileFormatException("Profile JSON root must be an object.");
            if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out int version))
                throw new ProfileFormatException("Profile schemaVersion is required and must be an integer.");
            if (version > LayoutProfile.CurrentSchemaVersion) throw new UnsupportedProfileVersionException(version);
            LayoutProfile profile = JsonSerializer.Deserialize<LayoutProfile>(json, Options)
                ?? throw new ProfileFormatException("Profile JSON contained no profile.");
            ProfileValidator.Validate(profile);
            return profile;
        }
        catch (ProfileFormatException) { throw; }
        catch (JsonException exception) { throw new ProfileFormatException("Profile JSON is malformed.", exception); }
        catch (NotSupportedException exception) { throw new ProfileFormatException("Profile JSON contains unsupported data.", exception); }
    }
}
