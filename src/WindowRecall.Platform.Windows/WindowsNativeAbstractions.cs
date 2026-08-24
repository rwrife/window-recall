namespace WindowRecall.Platform.Windows;

public enum NativeError { None, AccessDenied, NotFound, Rejected, Unsupported, InvalidData, Unknown }

#pragma warning disable CA1000 // Result factories are intentionally colocated with their closed generic result.
public readonly record struct NativeResult<T>(T? Value, NativeError Error, string Message)
{
    public bool IsSuccess => Error == NativeError.None;
    public static NativeResult<T> Success(T value) => new(value, NativeError.None, string.Empty);
    public static NativeResult<T> Failure(NativeError error, string message) => new(default, error, message);
}
#pragma warning restore CA1000

public readonly record struct NativeRect(int X, int Y, int Width, int Height);
public enum NativeWindowState { Normal, Minimized, Maximized }

public sealed record NativeMonitorInfo(
    long Id,
    string Name,
    NativeRect Bounds,
    NativeRect WorkArea,
    uint Dpi,
    bool IsPrimary);

public sealed record NativeWindowInfo(
    bool IsVisible,
    bool IsCloaked,
    bool IsToolWindow,
    nint Owner,
    uint ProcessId,
    string Title,
    string Role,
    string ApplicationId,
    string? ExecutablePath,
    NativeRect NormalBounds,
    NativeWindowState State,
    long MonitorId);

public interface IWindowsNativeApi
{
    bool IsSupported { get; }
    NativeResult<IReadOnlyList<nint>> EnumerateTopLevelWindows();
    NativeResult<IReadOnlyList<NativeMonitorInfo>> EnumerateMonitors();
    NativeResult<nint> GetShellWindow();
    NativeResult<NativeWindowInfo> ObserveWindow(nint handle);
    NativeResult<bool> SetWindowPosition(nint handle, NativeRect bounds);
    NativeResult<bool> ShowWindow(nint handle, NativeWindowState state);
}
