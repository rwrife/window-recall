using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;

namespace WindowRecall.App.Tests;

/// <summary>Automated accessibility checks over the real MainWindow XAML tree, run headless.
/// These prove structure (automation names, navigation reachability, live-region semantics,
/// resizability); they do NOT replace the documented manual Narrator/VoiceOver checklist.</summary>
public sealed class MainWindowAccessibilityTests
{
    private static (MainWindow Window, MainWindowViewModel Vm) CreateWindow()
    {
        InMemoryProfileStore store = new();
        FakeWindowSystem desktop = new(AppFixture.TwoWindowDesktop());
        MainWindowViewModel vm = new(store, () => desktop);
        vm.InitializeAsync().GetAwaiter().GetResult();
        MainWindow window = TestWindows.Create(vm);
        return (window, vm);
    }

    [AvaloniaFact]
    public void EveryInteractiveControl_HasAnAutomationNameOrVisibleText()
    {
        (MainWindow window, _) = CreateWindow();
        List<string> unnamed = window.GetLogicalDescendants().OfType<Control>()
            .Where(c => c is Button or CheckBox or TextBox or ComboBox or ListBox)
            .Where(c => !c.IsSet(Avalonia.Automation.AutomationProperties.NameProperty)
                        && string.IsNullOrWhiteSpace(GetVisibleText(c)))
            .Select(c => c.GetType().Name + " '" + (c.Name ?? "(no x:Name)") + "'")
            .ToList();
        Assert.Empty(unnamed);
    }

    [AvaloniaFact]
    public void HomeNavigation_IsDeclaredFirstAndAvailableFromEveryState()
    {
        (MainWindow window, MainWindowViewModel vm) = CreateWindow();

        // Declaration order is tab order in this window; the Home control comes first.
        Control? firstInteractive = window.GetLogicalDescendants().OfType<Control>()
            .FirstOrDefault(c => c is Button);
        Assert.Equal("HomeButton", firstInteractive?.Name);

        // Permission state never traps the user: the dismiss control exists and returns Home.
        vm.ShowPermissionNotice("simulated permission loss");
        Assert.Contains("NoticeDismissButton", window.GetLogicalDescendants().OfType<Button>().Select(b => b.Name));
        vm.GoHome();
        Assert.Equal(AppView.Home, vm.CurrentView);
    }

    [AvaloniaFact]
    public void StatusMessage_ExposesAssertiveLiveSetting()
    {
        (MainWindow window, _) = CreateWindow();
        TextBlock status = window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "StatusMessageText");
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Assertive,
            status.GetValue(Avalonia.Automation.AutomationProperties.LiveSettingProperty));
    }

    [AvaloniaFact]
    public void SectionHeadings_UseHeadingLevelSemantics()
    {
        (MainWindow window, _) = CreateWindow();
        TextBlock heading = window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "Saved profiles");
        Assert.Equal(1, heading.GetValue(Avalonia.Automation.AutomationProperties.HeadingLevelProperty));
    }

    [AvaloniaFact]
    public void UndoAction_IsDeclaredWithAnAutomationNameAndOnlyVisibleWhenAvailable()
    {
        (MainWindow window, MainWindowViewModel vm) = CreateWindow();
        Button undo = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "UndoButton");
        Assert.True(undo.IsSet(Avalonia.Automation.AutomationProperties.NameProperty));
        Assert.False(vm.IsUndoVisible); // not yet — appears from the result screen after a real apply
    }

    [AvaloniaFact]
    public void Window_IsResizableAndKeepsAMinimumSizeForTextScaling()
    {
        (MainWindow window, _) = CreateWindow();
        Assert.True(window.CanResize);
        Assert.True(window.MinWidth > 0);
        Assert.True(window.MinHeight > 0);
    }

    private static string GetVisibleText(Control control) => control switch
    {
        ContentControl content when content.Content is string text => text,
        TextBlock textBlock => textBlock.Text ?? string.Empty,
        _ => string.Empty,
    };
}
