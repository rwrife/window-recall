namespace WindowRecall.Core;

/// <summary>Portable normalized placement conversion and recoverability constraints.</summary>
public static class RestoreGeometry
{
    public const double MinimumVisibleWidth = 64;
    public const double MinimumVisibleHeight = 32;

    public static DesktopRect ConvertNormalized(DesktopRect bounds, DesktopRect savedWorkArea, DesktopRect currentWorkArea)
    {
        Validate(savedWorkArea, nameof(savedWorkArea));
        Validate(currentWorkArea, nameof(currentWorkArea));
        ArgumentNullException.ThrowIfNull(bounds);
        DesktopRect converted = new(
            currentWorkArea.X + ((bounds.X - savedWorkArea.X) / savedWorkArea.Width * currentWorkArea.Width),
            currentWorkArea.Y + ((bounds.Y - savedWorkArea.Y) / savedWorkArea.Height * currentWorkArea.Height),
            bounds.Width / savedWorkArea.Width * currentWorkArea.Width,
            bounds.Height / savedWorkArea.Height * currentWorkArea.Height);
        return ClampRecoverablyOnScreen(converted, currentWorkArea);
    }

    public static DesktopRect ClampRecoverablyOnScreen(DesktopRect bounds, DesktopRect workArea)
    {
        Validate(bounds, nameof(bounds));
        Validate(workArea, nameof(workArea));
        double width = Math.Min(bounds.Width, workArea.Width);
        double height = Math.Min(bounds.Height, workArea.Height);
        double visibleWidth = Math.Min(MinimumVisibleWidth, width);
        double visibleHeight = Math.Min(MinimumVisibleHeight, height);
        double x = Math.Clamp(bounds.X, workArea.X - width + visibleWidth, workArea.X + workArea.Width - visibleWidth);
        double y = Math.Clamp(bounds.Y, workArea.Y, workArea.Y + workArea.Height - visibleHeight);
        return new(x, y, width, height);
    }

    private static void Validate(DesktopRect rectangle, string parameter)
    {
        ArgumentNullException.ThrowIfNull(rectangle);
        if (!double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y) ||
            !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height) ||
            rectangle.Width <= 0 || rectangle.Height <= 0)
            throw new ArgumentOutOfRangeException(parameter, "Rectangle must be finite with positive dimensions.");
    }
}
