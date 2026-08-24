using WindowRecall.Core;

namespace WindowRecall.Platform.Windows;

/// <summary>Deterministic conversions between Win32 screen/workspace pixels and logical desktop coordinates.</summary>
public static class WindowsCoordinateConverter
{
    public static NativeRect WorkspaceToScreen(NativeRect workspaceRect, NativeRect monitorBounds, NativeRect workArea) =>
        workspaceRect with
        {
            X = checked(workspaceRect.X + workArea.X - monitorBounds.X),
            Y = checked(workspaceRect.Y + workArea.Y - monitorBounds.Y),
        };

    public static DesktopRect ToLogical(NativeRect rect, NativeRect monitorBounds, uint dpi) =>
        new(monitorBounds.X + ((rect.X - monitorBounds.X) * 96d / dpi),
            monitorBounds.Y + ((rect.Y - monitorBounds.Y) * 96d / dpi),
            rect.Width * 96d / dpi, rect.Height * 96d / dpi);

    public static NativeRect ToPhysical(DesktopRect rect, NativeRect monitorBounds, uint dpi) =>
        new(checked((int)Math.Round(monitorBounds.X + ((rect.X - monitorBounds.X) * dpi / 96d))),
            checked((int)Math.Round(monitorBounds.Y + ((rect.Y - monitorBounds.Y) * dpi / 96d))),
            checked((int)Math.Round(rect.Width * dpi / 96d)),
            checked((int)Math.Round(rect.Height * dpi / 96d)));
}
