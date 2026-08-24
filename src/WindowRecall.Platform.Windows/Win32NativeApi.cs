using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowRecall.Platform.Windows;

/// <summary>Narrow Win32 implementation. Every entry point is guarded so the assembly is safe on non-Windows CI.</summary>
public sealed class Win32NativeApi : IWindowsNativeApi
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const uint GwOwner = 4;
    private const uint DwmwaCloaked = 14;
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToNull = 0;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    public bool IsSupported => OperatingSystem.IsWindows();

    public NativeResult<IReadOnlyList<nint>> EnumerateTopLevelWindows()
    {
        if (!IsSupported) return Unsupported<IReadOnlyList<nint>>();
        List<nint> handles = [];
        bool success = EnumWindows((handle, _) => { handles.Add(handle); return true; }, 0);
        return success ? NativeResult<IReadOnlyList<nint>>.Success(handles) : LastError<IReadOnlyList<nint>>("EnumWindows");
    }

    public NativeResult<IReadOnlyList<NativeMonitorInfo>> EnumerateMonitors()
    {
        if (!IsSupported) return Unsupported<IReadOnlyList<NativeMonitorInfo>>();
        using DpiScope dpi = DpiScope.Enter();
        if (!dpi.Entered) return NativeResult<IReadOnlyList<NativeMonitorInfo>>.Success([]);
        List<NativeMonitorInfo> monitors = [];
        bool success = EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            MonitorInfoEx info = MonitorInfoEx.Create();
            if (!GetMonitorInfo(monitor, ref info)) return false;
            uint dpi = GetDpiForMonitorWindow(monitor);
            monitors.Add(new NativeMonitorInfo(monitor.ToInt64(), info.Device.TrimEnd('\0'), ToRect(info.Monitor),
                ToRect(info.Work), dpi, (info.Flags & 1) != 0));
            return true;
        }, 0);
        return success ? NativeResult<IReadOnlyList<NativeMonitorInfo>>.Success(monitors) : LastError<IReadOnlyList<NativeMonitorInfo>>("EnumDisplayMonitors");
    }

    public NativeResult<nint> GetShellWindow()
    {
        if (!IsSupported) return Unsupported<nint>();
        nint handle = NativeGetShellWindow();
        return handle != 0 ? NativeResult<nint>.Success(handle) : LastError<nint>("GetShellWindow");
    }

    public NativeResult<NativeWindowInfo> ObserveWindow(nint handle)
    {
        if (!IsSupported) return Unsupported<NativeWindowInfo>();
        using DpiScope dpi = DpiScope.Enter();
        if (!dpi.Entered) return DpiContextUnavailable<NativeWindowInfo>();
        if (!IsWindow(handle)) return NativeResult<NativeWindowInfo>.Failure(NativeError.NotFound, "The window handle is no longer valid.");
        bool visible = IsWindowVisible(handle);
        int cloaked = 0;
        int dwmResult = DwmGetWindowAttribute(handle, DwmwaCloaked, out cloaked, sizeof(int));
        if (dwmResult != 0) return HResultError<NativeWindowInfo>(dwmResult, "DwmGetWindowAttribute");
        long exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        nint owner = GetWindow(handle, GwOwner);
        _ = GetWindowThreadProcessId(handle, out uint processId);
        if (processId == 0) return LastError<NativeWindowInfo>("GetWindowThreadProcessId");
        NativeResult<(string Id, string Path)> identity = GetProcessIdentity(processId);
        if (!identity.IsSuccess) return NativeResult<NativeWindowInfo>.Failure(identity.Error, identity.Message);
        string title = GetTitle(handle);
        WindowPlacement placement = WindowPlacement.Create();
        if (!GetWindowPlacement(handle, ref placement)) return LastError<NativeWindowInfo>("GetWindowPlacement");
        nint monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == 0) return LastError<NativeWindowInfo>("MonitorFromWindow");
        MonitorInfoEx monitorInfo = MonitorInfoEx.Create();
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return LastError<NativeWindowInfo>("GetMonitorInfo");
        (string id, string path) = identity.Value;
        return NativeResult<NativeWindowInfo>.Success(new NativeWindowInfo(visible, cloaked != 0,
            (exStyle & WsExToolWindow) != 0, owner, processId, title, "top-level", id, path,
            WindowsCoordinateConverter.WorkspaceToScreen(ToRect(placement.NormalPosition), ToRect(monitorInfo.Monitor), ToRect(monitorInfo.Work)),
            placement.ShowCommand switch { 2 => NativeWindowState.Minimized, 3 => NativeWindowState.Maximized, _ => NativeWindowState.Normal },
            monitor.ToInt64()));
    }

    public NativeResult<bool> SetWindowPosition(nint handle, NativeRect bounds)
    {
        if (!IsSupported) return Unsupported<bool>();
        using DpiScope dpi = DpiScope.Enter();
        if (!dpi.Entered) return DpiContextUnavailable<bool>();
        if (!IsWindow(handle)) return NativeResult<bool>.Failure(NativeError.NotFound, "The window handle is no longer valid.");
        return SetWindowPos(handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoActivate | SwpNoZOrder)
            ? NativeResult<bool>.Success(true) : LastError<bool>("SetWindowPos");
    }

    public NativeResult<bool> ShowWindow(nint handle, NativeWindowState state)
    {
        if (!IsSupported) return Unsupported<bool>();
        if (!IsWindow(handle)) return NativeResult<bool>.Failure(NativeError.NotFound, "The window handle is no longer valid.");
        int command = state switch { NativeWindowState.Minimized => 6, NativeWindowState.Maximized => 3, _ => 9 };
        return ShowWindowAsync(handle, command) ? NativeResult<bool>.Success(true) : LastError<bool>("ShowWindowAsync");
    }

    private static NativeResult<(string Id, string Path)> GetProcessIdentity(uint processId)
    {
        nint process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process == 0) return LastError<(string, string)>("OpenProcess");
        try
        {
            StringBuilder path = new(32768);
            uint length = (uint)path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length)) return LastError<(string, string)>("QueryFullProcessImageName");
            string value = path.ToString();
            return NativeResult<(string, string)>.Success((value.ToUpperInvariant(), value));
        }
        finally { _ = CloseHandle(process); }
    }

    private static string GetTitle(nint handle)
    {
        int length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        StringBuilder title = new(length + 1);
        return GetWindowText(handle, title, title.Capacity) > 0 ? title.ToString() : string.Empty;
    }

    private static uint GetDpiForMonitorWindow(nint monitor)
    {
        uint result = 0;
        try
        {
            _ = EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window) && IsPerMonitorDpiAware(window) &&
                    MonitorFromWindow(window, MonitorDefaultToNull) == monitor)
                {
                    uint windowDpi = GetDpiForWindow(window);
                    if (windowDpi > 0)
                    {
                        result = windowDpi;
                        return false;
                    }
                }
                return true;
            }, 0);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return result;
    }

    private static bool IsPerMonitorDpiAware(nint window)
    {
        nint context = GetWindowDpiAwarenessContext(window);
        return context != 0 &&
            (AreDpiAwarenessContextsEqual(context, new nint(-3)) ||
             AreDpiAwarenessContextsEqual(context, new nint(-4)));
    }

    private static NativeRect ToRect(Rect value) => new(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);
    private static NativeResult<T> Unsupported<T>() => NativeResult<T>.Failure(NativeError.Unsupported, "Win32 is available only on Windows.");
    private static NativeResult<T> DpiContextUnavailable<T>() => NativeResult<T>.Failure(NativeError.Unsupported,
        "A per-monitor DPI awareness context could not be established safely.");
    private static NativeResult<T> LastError<T>(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        return NativeResult<T>.Failure(WindowsNativeErrorMapper.FromWin32(error), $"{operation} failed: {new Win32Exception(error).Message}");
    }
    private static NativeResult<T> HResultError<T>(int hresult, string operation)
    {
        int error = hresult & 0xFFFF;
        return NativeResult<T>.Failure(WindowsNativeErrorMapper.FromWin32(error), $"{operation} failed (0x{hresult:X8}).");
    }

    private readonly struct DpiScope : IDisposable
    {
        private readonly nint previous;
        private DpiScope(nint previous) => this.previous = previous;
        public bool Entered => previous != 0;
        public static DpiScope Enter() => new(SetThreadDpiAwarenessContext(new nint(-4)));
        public void Dispose()
        {
            if (previous != 0) _ = SetThreadDpiAwarenessContext(previous);
        }
    }

#pragma warning disable SYSLIB1054, CA1838
    private delegate bool EnumWindowsProc(nint hwnd, nint parameter);
    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", EntryPoint = "GetShellWindow")] private static extern nint NativeGetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
#pragma warning restore SYSLIB1054, CA1838

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size; public Rect Monitor; public Rect Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        public static MonitorInfoEx Create() => new() { Size = (uint)Marshal.SizeOf<MonitorInfoEx>(), Device = string.Empty };
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length, Flags, ShowCommand; public Point MinPosition, MaxPosition; public Rect NormalPosition;
        public static WindowPlacement Create() => new() { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
}
