using Avalonia.Controls;

namespace WindowRecall.App;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Live composition root (file profile store + live desktop adapter).</summary>
    public MainWindow(MainWindowViewModel viewModel) : this() => DataContext = viewModel;
}
