using WindowRecall.Platform.Windows;

namespace WindowRecall.Windows.Tests;

public sealed class WindowsWindowSystemTests
{
    [Fact]
    public async Task MilestoneOneSeam_DoesNotClaimLiveCapabilities()
    {
        WindowsWindowSystem system = new();
        var capabilities = await system.GetCapabilitiesAsync();
        Assert.False(capabilities.CanObserve);
        Assert.False(capabilities.CanMoveResize);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => system.CaptureAsync());
    }
}
