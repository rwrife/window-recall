using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace WindowRecall.App;

/// <summary>Inverts a bool so one bound property can drive both "include" checkboxes and "exclude" state.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag ? !flag : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag ? !flag : BindingOperations.DoNothing;
}

/// <summary>Maps a launch-policy row to a checkbox (checked == explicit launch allowed).</summary>
public sealed class LaunchPolicyToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LaunchPolicyRow policy ? policy is LaunchPolicyRow.AllowExplicitLaunch : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool allowed ? (allowed ? LaunchPolicyRow.AllowExplicitLaunch : LaunchPolicyRow.Never) : BindingOperations.DoNothing;
}

/// <summary>True when a collection has items; used to reveal ambiguity pickers only on ambiguous rows.</summary>
public sealed class NonEmptyCollectionToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is System.Collections.ICollection collection ? collection.Count > 0 : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>True when a value is null; used for indeterminate progress.</summary>
public sealed class IsNullToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>Builds the live composition root. Native platform types are referenced only here; all
/// workflow logic stays behind Core interfaces so it remains testable off-desktop.</summary>
public static class DesktopViewModelFactory
{
    public static MainWindowViewModel Create()
    {
        string dataRoot = Cli.CliPaths.ResolveDataRoot(null);
        return new MainWindowViewModel(
            new Core.JsonFileProfileStore(Cli.CliPaths.ProfilesDirectory(dataRoot)),
            Cli.LiveDesktop.Open,
            CreatePermissionProvider());
    }

    private static MacOSPermissionStatusProvider? CreatePermissionProvider() =>
        OperatingSystem.IsMacOS() ? new MacOSPermissionStatusProvider() : null;

    /// <summary>Surfaces macOS Accessibility permission state and the settings shortcut without ever
    /// blocking the UI; Window Recall intentionally never requests Screen Recording or admin rights.</summary>
    internal sealed class MacOSPermissionStatusProvider : IPermissionStatusProvider
    {
        private readonly Platform.MacOS.MacOSAccessibilityOnboardingService service = new();

        public async Task<PermissionBanner> GetBannerAsync(CancellationToken cancellationToken = default)
        {
            Platform.MacOS.MacOSAccessibilityPermissionStatus status =
                await service.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            string title = status.IsGranted
                ? "Accessibility permission granted"
                : "Accessibility permission not granted — read-only mode";
            return new PermissionBanner(title, status.Explanation, status.CanOpenSystemSettings);
        }

        public Task<bool> OpenSystemSettingsAsync(CancellationToken cancellationToken = default) =>
            service.OpenSystemSettingsAsync(cancellationToken);
    }
}
