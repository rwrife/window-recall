namespace WindowRecall.Platform.MacOS;

/// <summary>UI-facing Accessibility permission state without native objects.</summary>
public sealed record MacOSAccessibilityPermissionStatus(
    bool IsGranted,
    bool CanOpenSystemSettings,
    string Explanation);

/// <summary>Contract consumed by a UI view model for macOS permission onboarding.</summary>
public interface IMacOSAccessibilityOnboardingService
{
    /// <summary>Returns current permission and user-facing guidance.</summary>
    Task<MacOSAccessibilityPermissionStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Attempts to open the Accessibility privacy pane through the native URL API.</summary>
    Task<bool> OpenSystemSettingsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Native-backed permission onboarding service.</summary>
public sealed class MacOSAccessibilityOnboardingService : IMacOSAccessibilityOnboardingService
{
    private const string Explanation =
        "Accessibility permission lets Window Recall inspect supported window state and move or resize windows. " +
        "Without it, Window Recall remains useful in read-only mode using CoreGraphics metadata. " +
        "Screen Recording is not required because Window Recall never reads screen pixels.";

    private readonly IMacOSNativeApi native;

    /// <summary>Creates the live onboarding service.</summary>
    public MacOSAccessibilityOnboardingService()
        : this(new MacOSNativeApi())
    {
    }

    internal MacOSAccessibilityOnboardingService(IMacOSNativeApi native) => this.native = native;

    /// <inheritdoc />
    public Task<MacOSAccessibilityPermissionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MacOSAccessibilityPermissionStatus(
            native.GetAccessibilityTrust(),
            OperatingSystem.IsMacOS() || native is not MacOSNativeApi,
            Explanation));
    }

    /// <inheritdoc />
    public Task<bool> OpenSystemSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(native.OpenAccessibilitySettings());
    }
}
