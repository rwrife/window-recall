using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(WindowRecall.App.Tests.TestAppBuilder))]

namespace WindowRecall.App.Tests;

/// <summary>Minimal Avalonia application used by headless UI tests. It deliberately does NOT use
/// the production App class so tests never touch the real profile store or live desktop adapter.</summary>
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window();
        base.OnFrameworkInitializationCompleted();
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>Loads the real MainWindow.axaml with an injected view model, exactly as production does,
/// but with fake stores/adapters. Proves the XAML tree, bindings, and automation tree load without
/// a display server.</summary>
internal static class TestWindows
{
    public static MainWindow Create(MainWindowViewModel viewModel)
    {
        MainWindow window = new(viewModel);
        window.Show();
        return window;
    }
}
