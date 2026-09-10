using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace WindowRecall.MacOSFixture;

internal static class Program
{
    [STAThread]
    private static void Main() => Build().StartWithClassicDesktopLifetime([]);

    private static AppBuilder Build() => AppBuilder.Configure<FixtureApplication>().UsePlatformDetect();
}

internal sealed class FixtureApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = Create("Alpha", 80, 80);
            desktop.MainWindow.Opened += (_, _) =>
            {
                Create("Beta", 180, 160).Show();
                Create("Gamma", 280, 240).Show();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Window Create(string label, int x, int y) => new()
    {
        Title = $"Window Recall macOS Fixture — {label}",
        Width = 600,
        Height = 400,
        Position = new PixelPoint(x, y),
        Content = new TextBlock { Text = label, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Left },
    };
}
