using WindowRecall.Platform.Windows;

namespace WindowRecall.Windows.Tests;

public sealed class WindowsNativeErrorMapperTests
{
    [Theory]
    [InlineData(5, NativeError.AccessDenied)]
    [InlineData(6, NativeError.NotFound)]
    [InlineData(1400, NativeError.NotFound)]
    [InlineData(50, NativeError.Unsupported)]
    [InlineData(87, NativeError.InvalidData)]
    [InlineData(0, NativeError.Rejected)]
    [InlineData(12345, NativeError.Unknown)]
    public void FromWin32_MapsStableStructuredErrors(int win32Error, NativeError expected) =>
        Assert.Equal(expected, WindowsNativeErrorMapper.FromWin32(win32Error));
}
