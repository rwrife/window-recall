using WindowRecall.Platform.MacOS;

namespace WindowRecall.MacOS.Tests;

public sealed class MacOSAccessibilityOnboardingTests
{
    [Fact]
    public async Task Status_ExplainsReadOnlyModeAndThatScreenRecordingIsNotRequired()
    {
        FakeMacOSNativeApi native = new() { IsAccessibilityTrusted = false };
        MacOSAccessibilityOnboardingService service = new(native);

        MacOSAccessibilityPermissionStatus status = await service.GetStatusAsync();

        Assert.False(status.IsGranted);
        Assert.True(status.CanOpenSystemSettings);
        Assert.Contains("move", status.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Screen Recording is not required", status.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenSettings_UsesNativeDeepLinkAndReturnsWhetherItWasSupported()
    {
        FakeMacOSNativeApi native = new() { OpenSettingsResult = true };
        MacOSAccessibilityOnboardingService service = new(native);

        bool opened = await service.OpenSystemSettingsAsync();

        Assert.True(opened);
        Assert.Equal(1, native.OpenSettingsCount);
    }
}
