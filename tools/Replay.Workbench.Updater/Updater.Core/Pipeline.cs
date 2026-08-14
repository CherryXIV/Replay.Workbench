using System.Diagnostics;

namespace ReplayWorkbench.Updater;

/// <summary>The files a patch update rewrites, relative to the repo root.</summary>
public static class TrackedFiles
{
    public static readonly string[] All =
    {
        "docs/opcodes.js",
        "docs/patchdiffs.js",
        "docs/old/opcodes.js",
        "tools/build_patchdiffs.py",
        "tools/bump_replay.py",
        "tools/replay_builds.json",
        "programs/Replay.Workbench.Core/Data/opcodes.json",
        "programs/Replay.Workbench.Core/Data/patchdiffs.json",
        "programs/Replay.Workbench.Core/Data/afgear.json",
    };

    /// <summary>
    /// Copy every tracked file into tools/.update-backups/&lt;timestamp&gt;/ before the
    /// update touches them. Git is the real safety net, but these files routinely
    /// carry uncommitted work, so the update brings its own.
    /// </summary>
    public static string Backup(string repoRoot, Action<string> log)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dest = Path.Combine(repoRoot, "tools", ".update-backups", stamp);
        var saved = 0;
        foreach (var rel in All)
        {
            var src = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            var to = Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(src, to, overwrite: true);
            saved++;
        }
        log($"backed up {saved} file(s) to tools/.update-backups/{stamp}");
        return dest;
    }

    /// <summary>Put a backup back, for when an update turns out to be wrong.</summary>
    public static int Restore(string repoRoot, string backupDir, Action<string> log)
    {
        var restored = 0;
        foreach (var rel in All)
        {
            var src = Path.Combine(backupDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            var to = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(src, to, overwrite: true);
            restored++;
        }
        log($"restored {restored} file(s) from {Path.GetFileName(backupDir)}");
        return restored;
    }
}

/// <summary>Runs the child processes the update needs: dotnet, and the verifier.</summary>
public static class Pipeline
{
    /// <summary>
    /// The editor holds its own binaries open, so a rebuild fails with MSB3027
    /// while it is running. Catch that before starting instead of half way through.
    /// </summary>
    public static List<Process> RunningEditors()
    {
        try { return Process.GetProcessesByName("Replay.Workbench").ToList(); }
        catch { return new List<Process>(); }
    }

    public static int Run(string exe, string args, string workingDir, Action<string> log)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Rebuild the editor solution so it picks up the regenerated data.</summary>
    public static bool BuildEditor(string repoRoot, string configuration, Action<string> log)
    {
        var running = RunningEditors();
        if (running.Count > 0)
        {
            log($"error: Replay.Workbench is running (pid {string.Join(", ", running.Select(p => p.Id))}); " +
                "close it before rebuilding — it holds its own assemblies open.");
            return false;
        }
        var sln = Path.Combine(repoRoot, "programs", "Replay.Workbench.sln");
        log($"dotnet build {Path.GetRelativePath(repoRoot, sln)} -c {configuration}");
        var code = Run("dotnet", $"build \"{sln}\" -c {configuration} --nologo", repoRoot, log);
        log(code == 0 ? "build succeeded" : $"build FAILED (exit {code})");
        return code == 0;
    }

    /// <summary>
    /// Run the post-update checks against a recording on the new patch. The verifier
    /// is built by the step before this one, so it always tests the data that was
    /// just written rather than whatever was compiled in earlier.
    /// </summary>
    public static bool Verify(string repoRoot, string configuration, string? recording, Action<string> log)
    {
        var exe = Path.Combine(repoRoot, "programs", "Replay.Workbench.Verify",
            "bin", configuration, "net9.0", "Replay.Workbench.Verify.exe");
        if (!File.Exists(exe))
        {
            log($"error: verifier not found at {Path.GetRelativePath(repoRoot, exe)} — did the build run?");
            return false;
        }
        var args = string.IsNullOrWhiteSpace(recording) ? "" : $"\"{recording}\"";
        var code = Run(exe, args, repoRoot, log);
        return code == 0;
    }
}
