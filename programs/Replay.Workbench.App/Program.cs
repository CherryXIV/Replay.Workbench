namespace ReplayWorkbench.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        // One optional argument: a recording to open on startup, so the exe can be
        // set as the .dat "open with" handler or dropped onto from Explorer.
        var open = args.FirstOrDefault(a => a.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        Application.Run(new MainForm(open));
    }
}
