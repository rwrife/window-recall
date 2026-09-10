namespace WindowRecall.Cli;

/// <summary>Entry point for the deterministic headless command surface.</summary>
public static class Program
{
    public static Task<int> Main(string[] args) =>
        new CliRunner(Console.Out, Console.Error).RunAsync(args);
}
