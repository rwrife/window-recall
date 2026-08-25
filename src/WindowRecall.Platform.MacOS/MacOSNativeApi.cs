using System.Collections.Immutable;
using System.Runtime.InteropServices;
using WindowRecall.Core;

namespace WindowRecall.Platform.MacOS;

/// <summary>CoreGraphics observation and authorized Accessibility mutation implementation.</summary>
internal sealed class MacOSNativeApi : IMacOSNativeApi
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private const string LibProc = "/usr/lib/libproc.dylib";
    private static readonly Lazy<nint> CoreFoundationHandle = new(() => NativeLibrary.Load(CoreFoundation));

    public bool GetAccessibilityTrust() => OperatingSystem.IsMacOS() && AXIsProcessTrusted();

    public ImmutableArray<MacOSDisplayObservation> ObserveDisplays()
    {
        EnsureMacOS();
        uint[] ids = new uint[32];
        int error = CGGetActiveDisplayList((uint)ids.Length, ids, out uint count);
        if (error != 0) throw new InvalidOperationException($"CGGetActiveDisplayList failed ({error}).");
        ImmutableArray<MacOSDisplayObservation>.Builder result = ImmutableArray.CreateBuilder<MacOSDisplayObservation>((int)count);
        uint main = CGMainDisplayID();
        for (int index = 0; index < count; index++)
        {
            uint id = ids[index];
            CGRect bounds = CGDisplayBounds(id);
            double width = CGDisplayPixelsWide(id);
            double scale = bounds.Size.Width > 0 ? width / bounds.Size.Width : 1;
            result.Add(new MacOSDisplayObservation(id, null, ToRect(bounds), ToRect(bounds), scale,
                bounds.Size.Width >= bounds.Size.Height ? DisplayOrientation.Landscape : DisplayOrientation.Portrait, id == main));
        }

        return result.ToImmutable();
    }

    public ImmutableArray<MacOSWindowObservation> ObserveWindows(bool includeAccessibilityDetails)
    {
        EnsureMacOS();
        nint array = CGWindowListCopyWindowInfo(0, 0);
        if (array == 0) return [];
        try
        {
            Dictionary<int, ApplicationInfo> applications = [];
            ImmutableArray<MacOSWindowObservation>.Builder result = ImmutableArray.CreateBuilder<MacOSWindowObservation>();
            nint count = CFArrayGetCount(array);
            for (nint index = 0; index < count; index++)
            {
                nint dictionary = CFArrayGetValueAtIndex(array, index);
                uint windowId = (uint)GetNumber(dictionary, "kCGWindowNumber");
                int pid = (int)GetNumber(dictionary, "kCGWindowOwnerPID");
                int layer = (int)GetNumber(dictionary, "kCGWindowLayer");
                bool onScreen = GetBoolean(dictionary, "kCGWindowIsOnscreen", true);
                DesktopRect bounds = GetBounds(dictionary);
                if (!applications.TryGetValue(pid, out ApplicationInfo? application))
                {
                    application = GetApplicationInfo(pid);
                    applications.Add(pid, application);
                }

                AxDetails ax = includeAccessibilityDetails ? GetAxDetails(pid, bounds) : AxDetails.ReadOnly;
                bool systemOwned = application.BundleIdentifier is "com.apple.dock" or "com.apple.systemuiserver" or
                    "com.apple.controlcenter" or "com.apple.notificationcenterui";
                result.Add(new MacOSWindowObservation(windowId, pid, application.BundleIdentifier, application.ExecutableUrl,
                    bounds, onScreen, layer, systemOwned, ax.IsModal, ax.IsHidden, ax.IsFullScreen, ax.IsMinimized,
                    ax.SupportsPosition, ax.SupportsSize, ax.State, FindDisplay(bounds)));
            }

            return result.ToImmutable();
        }
        finally
        {
            CFRelease(array);
        }
    }

    public MacOSMutationResult SetWindowBounds(
        MacOSWindowIdentity identity,
        DesktopRect bounds,
        CancellationToken cancellationToken)
    {
        if (!GetAccessibilityTrust()) return new(MacOSNativeError.PermissionDenied);
        MacOSWindowObservation? observation = ReobserveWindow(identity.WindowId);
        if (observation is null || !SameIdentity(observation, identity)) return new(MacOSNativeError.StaleElement);
        if (cancellationToken.IsCancellationRequested) return new(MacOSNativeError.Cancelled);
        nint element = FindAxWindow(observation);
        if (element == 0) return new(MacOSNativeError.StaleElement);
        try
        {
            if (cancellationToken.IsCancellationRequested) return new(MacOSNativeError.Cancelled);
            CGPoint point = new(bounds.X, bounds.Y);
            CGSize size = new(bounds.Width, bounds.Height);
            MacOSNativeError positionError = SetAxPointValue(element, "AXPosition", 1, ref point);
            if (positionError is not MacOSNativeError.None)
            {
                return new(positionError, $"AXPosition failed ({positionError}).");
            }

            MacOSNativeError sizeError = SetAxSizeValue(element, "AXSize", 2, ref size);
            return sizeError is MacOSNativeError.None
                ? MacOSMutationResult.Success
                : new(sizeError, $"Partial apply: AXPosition succeeded; AXSize failed ({sizeError}).");
        }
        finally { CFRelease(element); }
    }

    public MacOSMutationResult SetWindowState(
        MacOSWindowIdentity identity,
        WindowState state,
        CancellationToken cancellationToken)
    {
        if (!GetAccessibilityTrust()) return new(MacOSNativeError.PermissionDenied);
        MacOSWindowObservation? observation = ReobserveWindow(identity.WindowId);
        if (observation is null || !SameIdentity(observation, identity)) return new(MacOSNativeError.StaleElement);
        if (cancellationToken.IsCancellationRequested) return new(MacOSNativeError.Cancelled);
        nint element = FindAxWindow(observation);
        if (element == 0) return new(MacOSNativeError.StaleElement);
        try
        {
            if (cancellationToken.IsCancellationRequested) return new(MacOSNativeError.Cancelled);
            nint value = state == WindowState.Minimized ? GetBooleanConstant(true) : GetBooleanConstant(false);
            nint attribute = CreateString("AXMinimized");
            try
            {
                int error = AXUIElementSetAttributeValue(element, attribute, value);
                return error == 0 ? MacOSMutationResult.Success : new(MapAxError(error));
            }
            finally { CFRelease(attribute); }
        }
        finally { CFRelease(element); }
    }

    public MacOSWindowObservation? ReobserveWindow(uint windowId) =>
        ObserveWindows(includeAccessibilityDetails: true).FirstOrDefault(window => window.WindowId == windowId);

    private static bool SameIdentity(MacOSWindowObservation observation, MacOSWindowIdentity identity) =>
        observation.WindowId == identity.WindowId &&
        observation.OwnerPid == identity.OwnerPid &&
        StringComparer.Ordinal.Equals(observation.BundleIdentifier, identity.BundleIdentifier) &&
        StringComparer.Ordinal.Equals(observation.ExecutableUrl, identity.ExecutableUrl);

    public bool OpenAccessibilitySettings()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        nint workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
        nint urlClass = objc_getClass("NSURL");
        using CfString url = new("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
        nint nativeUrl = Send(urlClass, sel_registerName("URLWithString:"), url.Value);
        return SendBool(workspace, sel_registerName("openURL:"), nativeUrl);
    }

    private static AxDetails GetAxDetails(int pid, DesktopRect bounds)
    {
        nint application = AXUIElementCreateApplication(pid);
        if (application == 0) return AxDetails.ReadOnly;
        try
        {
            nint window = FindAxWindow(application, bounds);
            if (window == 0) return AxDetails.ReadOnly;
            try
            {
                bool minimized = GetAxBoolean(window, "AXMinimized");
                bool fullScreen = GetAxBoolean(window, "AXFullScreen");
                bool modal = GetAxBoolean(window, "AXModal");
                return new AxDetails(modal, false, fullScreen, minimized,
                    IsAxSettable(window, "AXPosition"), IsAxSettable(window, "AXSize"),
                    fullScreen ? WindowState.FullScreen : minimized ? WindowState.Minimized : WindowState.Normal);
            }
            finally { CFRelease(window); }
        }
        finally { CFRelease(application); }
    }

    private static nint FindAxWindow(MacOSWindowObservation observation)
    {
        nint application = AXUIElementCreateApplication(observation.OwnerPid);
        if (application == 0) return 0;
        try { return FindAxWindow(application, observation.Bounds); }
        finally { CFRelease(application); }
    }

    private static nint FindAxWindow(nint application, DesktopRect target)
    {
        nint attribute = CreateString("AXWindows");
        int error = AXUIElementCopyAttributeValue(application, attribute, out nint windows);
        CFRelease(attribute);
        if (error != 0 || windows == 0) return 0;
        try
        {
            nint match = 0;
            int matchCount = 0;
            nint count = CFArrayGetCount(windows);
            for (nint index = 0; index < count; index++)
            {
                nint candidate = CFArrayGetValueAtIndex(windows, index);
                if (!TryGetAxRect(candidate, out DesktopRect rect) || !Near(rect, target))
                {
                    continue;
                }

                match = candidate;
                matchCount++;
                if (matchCount > 1)
                {
                    return 0;
                }
            }

            if (matchCount == 1)
            {
                CFRetain(match);
                return match;
            }

            return 0;
        }
        finally { CFRelease(windows); }
    }

    private static bool TryGetAxRect(nint element, out DesktopRect rect)
    {
        rect = new DesktopRect(0, 0, 0, 0);
        if (!CopyAxValue(element, "AXPosition", out nint position)) return false;
        try
        {
            if (!CopyAxValue(element, "AXSize", out nint size)) return false;
            try
            {
                CGPoint point = default;
                CGSize dimensions = default;
                if (!AXValueGetPoint(position, 1, ref point) || !AXValueGetSize(size, 2, ref dimensions)) return false;
                rect = new DesktopRect(point.X, point.Y, dimensions.Width, dimensions.Height);
                return true;
            }
            finally { CFRelease(size); }
        }
        finally { CFRelease(position); }
    }

    private static bool CopyAxValue(nint element, string name, out nint value)
    {
        nint attribute = CreateString(name);
        int error = AXUIElementCopyAttributeValue(element, attribute, out value);
        CFRelease(attribute);
        return error == 0 && value != 0;
    }

    private static bool GetAxBoolean(nint element, string name)
    {
        if (!CopyAxValue(element, name, out nint value)) return false;
        try { return CFBooleanGetValue(value); }
        finally { CFRelease(value); }
    }

    private static bool IsAxSettable(nint element, string name)
    {
        nint attribute = CreateString(name);
        int error = AXUIElementIsAttributeSettable(element, attribute, out bool settable);
        CFRelease(attribute);
        return error == 0 && settable;
    }

    private static MacOSNativeError SetAxPointValue(nint element, string name, int type, ref CGPoint value)
    {
        nint axValue = AXValueCreatePoint(type, ref value);
        if (axValue == 0) return MacOSNativeError.Failed;
        nint attribute = CreateString(name);
        try { return MapAxError(AXUIElementSetAttributeValue(element, attribute, axValue)); }
        finally { CFRelease(attribute); CFRelease(axValue); }
    }

    private static MacOSNativeError SetAxSizeValue(nint element, string name, int type, ref CGSize value)
    {
        nint axValue = AXValueCreateSize(type, ref value);
        if (axValue == 0) return MacOSNativeError.Failed;
        nint attribute = CreateString(name);
        try { return MapAxError(AXUIElementSetAttributeValue(element, attribute, axValue)); }
        finally { CFRelease(attribute); CFRelease(axValue); }
    }

    internal static MacOSNativeError MapAxError(int error) => error switch
    {
        0 => MacOSNativeError.None,
        -25211 => MacOSNativeError.PermissionDenied, // kAXErrorAPIDisabled
        -25202 or -25203 => MacOSNativeError.StaleElement,
        -25205 or -25206 or -25207 or -25208 or -25212 or -25213 => MacOSNativeError.Unsupported,
        -25201 or -25204 => MacOSNativeError.Rejected,
        _ => MacOSNativeError.Failed,
    };

    private static ApplicationInfo GetApplicationInfo(int pid)
    {
        nint application = Send(objc_getClass("NSRunningApplication"), sel_registerName("runningApplicationWithProcessIdentifier:"), pid);
        string? bundle = GetNSString(Send(application, sel_registerName("bundleIdentifier")));
        byte[] buffer = new byte[4096];
        int length = proc_pidpath(pid, buffer, (uint)buffer.Length);
        string? path = length > 0 ? System.Text.Encoding.UTF8.GetString(buffer, 0, length) : null;
        return new ApplicationInfo(bundle, path is null ? null : new Uri(path).AbsoluteUri);
    }

    private static string? GetNSString(nint value) => value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, sel_registerName("UTF8String")));
    private static uint? FindDisplay(DesktopRect bounds)
    {
        foreach ((uint id, DesktopRect displayBounds) in ObserveDisplayIds())
        {
            if (Intersects(displayBounds, bounds)) return id;
        }

        return null;
    }
    private static IEnumerable<(uint Id, DesktopRect Bounds)> ObserveDisplayIds()
    {
        uint[] ids = new uint[32];
        if (CGGetActiveDisplayList((uint)ids.Length, ids, out uint count) != 0) yield break;
        for (int index = 0; index < count; index++) yield return (ids[index], ToRect(CGDisplayBounds(ids[index])));
    }

    private static bool Intersects(DesktopRect first, DesktopRect second) => first.X < second.X + second.Width && second.X < first.X + first.Width && first.Y < second.Y + second.Height && second.Y < first.Y + first.Height;
    private static bool Near(DesktopRect first, DesktopRect second) => Math.Abs(first.X - second.X) <= 2 && Math.Abs(first.Y - second.Y) <= 2 && Math.Abs(first.Width - second.Width) <= 2 && Math.Abs(first.Height - second.Height) <= 2;
    private static DesktopRect ToRect(CGRect value) => new(value.Origin.X, value.Origin.Y, value.Size.Width, value.Size.Height);

    private static DesktopRect GetBounds(nint dictionary)
    {
        nint bounds = GetDictionaryValue(dictionary, "kCGWindowBounds");
        return new DesktopRect(GetNumber(bounds, "X"), GetNumber(bounds, "Y"), GetNumber(bounds, "Width"), GetNumber(bounds, "Height"));
    }

    private static double GetNumber(nint dictionary, string key)
    {
        nint value = GetDictionaryValue(dictionary, key);
        double number = 0;
        return value != 0 && CFNumberGetValue(value, 13, ref number) ? number : 0;
    }

    private static bool GetBoolean(nint dictionary, string key, bool fallback)
    {
        nint value = GetDictionaryValue(dictionary, key);
        return value == 0 ? fallback : CFBooleanGetValue(value);
    }

    private static nint GetDictionaryValue(nint dictionary, string key)
    {
        using CfString nativeKey = new(key);
        return CFDictionaryGetValue(dictionary, nativeKey.Value);
    }

    private static nint CreateString(string value) => CFStringCreateWithCString(0, value, 0x08000100);
    private static nint GetBooleanConstant(bool value) => CFBooleanGetValueAddress(value);
    private static nint CFBooleanGetValueAddress(bool value)
    {
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(
            CoreFoundationHandle.Value,
            value ? "kCFBooleanTrue" : "kCFBooleanFalse"));
    }

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The macOS adapter can execute only on macOS.");
    }

    private sealed record ApplicationInfo(string? BundleIdentifier, string? ExecutableUrl);
    private sealed record AxDetails(bool IsModal, bool IsHidden, bool IsFullScreen, bool IsMinimized, bool SupportsPosition, bool SupportsSize, WindowState State)
    {
        public static AxDetails ReadOnly { get; } = new(false, false, false, false, false, false, WindowState.Normal);
    }

    private readonly struct CfString : IDisposable
    {
        public CfString(string value) => Value = CreateString(value);
        public nint Value { get; }
        public void Dispose() { if (Value != 0) CFRelease(Value); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public CGPoint(double x, double y) { X = x; Y = y; } public double X; public double Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CGSize { public CGSize(double width, double height) { Width = width; Height = height; } public double Width; public double Height; }
    [StructLayout(LayoutKind.Sequential)] private struct CGRect { public CGPoint Origin; public CGSize Size; }

#pragma warning disable SYSLIB1054 // Generated marshalling is not available for these CoreFoundation/Objective-C signatures.
#pragma warning disable CA2101 // These native APIs require explicitly marshalled UTF-8 narrow strings, not UTF-16.
    [DllImport(CoreGraphics)] private static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] displays, out uint count);
    [DllImport(CoreGraphics)] private static extern uint CGMainDisplayID();
    [DllImport(CoreGraphics)] private static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CoreGraphics)] private static extern nuint CGDisplayPixelsWide(uint display);
    [DllImport(CoreGraphics)] private static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();
    [DllImport(ApplicationServices)] private static extern nint AXUIElementCreateApplication(int pid);
    [DllImport(ApplicationServices)] private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
    [DllImport(ApplicationServices)] private static extern int AXUIElementSetAttributeValue(nint element, nint attribute, nint value);
    [DllImport(ApplicationServices)] private static extern int AXUIElementIsAttributeSettable(nint element, nint attribute, [MarshalAs(UnmanagedType.I1)] out bool settable);
    [DllImport(ApplicationServices, EntryPoint = "AXValueCreate")] private static extern nint AXValueCreatePoint(int type, ref CGPoint value);
    [DllImport(ApplicationServices, EntryPoint = "AXValueCreate")] private static extern nint AXValueCreateSize(int type, ref CGSize value);
    [DllImport(ApplicationServices, EntryPoint = "AXValueGetValue")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXValueGetPoint(nint value, int type, ref CGPoint output);
    [DllImport(ApplicationServices, EntryPoint = "AXValueGetValue")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXValueGetSize(nint value, int type, ref CGSize output);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(nint number, int type, ref double value);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFBooleanGetValue(nint value);
    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(
        nint allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFRetain(nint value);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    [DllImport(LibProc)] private static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint bufferSize);
    [DllImport(ObjectiveC)] private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjectiveC)] private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, int argument);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint argument);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector, nint argument);
#pragma warning restore CA2101
#pragma warning restore SYSLIB1054
}
