using WindowRecall.Platform.MacOS;

namespace WindowRecall.MacOS.Tests;

public sealed class MacOSWindowSystemTests
{
    [Fact]
    public async Task MilestoneOneSeam_DoesNotClaimLiveCapabilities()
    {
        MacOSWindowSystem system = new();
        var capabilities = await system.GetCapabilitiesAsync();
        Assert.False(capabilities.CanObserve);
        Assert.False(capabilities.CanMoveResize);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => system.CaptureAsync());
    }
}
