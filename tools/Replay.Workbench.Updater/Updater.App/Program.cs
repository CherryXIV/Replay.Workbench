namespace ReplayWorkbench.Updater.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);

        var root = args.FirstOrDefault(Directory.Exists) ?? FindRepoRoot();
        if (root is null)
        {
            MessageBox.Show(
                "Could not find the Replay.Workbench checkout.\n\n" +
                "Run this from inside the repo, or pass the repo root as an argument.",
                "Replay Workbench — patch update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new MainForm(root));
    }

    /// <summary>Walk up from the exe until the repo's own landmarks show up.</summary>
    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "tools", "build_patchdiffs.py")) &&
                Directory.Exists(Path.Combine(dir, "docs")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }
}
