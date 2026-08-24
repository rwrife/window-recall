namespace WindowRecall.Platform.Windows;

public static class WindowsNativeErrorMapper
{
    public static NativeError FromWin32(int error) => error switch
    {
        5 => NativeError.AccessDenied,
        6 or 1400 => NativeError.NotFound,
        50 or 120 => NativeError.Unsupported,
        87 => NativeError.InvalidData,
        0 => NativeError.Rejected,
        _ => NativeError.Unknown,
    };
}
