using System.Drawing;
using System.Windows.Forms;

namespace WindowRecall.WindowsFixture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        FixtureContext context = new(args.Length == 1 ? args[0] : string.Empty);
        Application.Run(context);
    }
}

internal sealed class FixtureContext : ApplicationContext
{
    private int openWindows = 3;

    public FixtureContext(string instanceId)
    {
        string suffix = string.IsNullOrWhiteSpace(instanceId) ? string.Empty : $" — {instanceId}";
        ShowWindow($"Window Recall Fixture — Alpha{suffix}", new Rectangle(80, 80, 640, 420));
        ShowWindow($"Window Recall Fixture — Beta{suffix}", new Rectangle(180, 160, 600, 400));
        ShowWindow($"Window Recall Fixture — Gamma{suffix}", new Rectangle(280, 240, 560, 380));
    }

    private void ShowWindow(string title, Rectangle bounds)
    {
        Form form = new() { Text = title, StartPosition = FormStartPosition.Manual, Bounds = bounds };
        form.FormClosed += (_, _) => { if (--openWindows == 0) ExitThread(); };
        form.Controls.Add(new Label { Text = title, AutoSize = true, Location = new Point(24, 24) });
        form.Show();
    }
}
