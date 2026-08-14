using System.Text.Json;

namespace ReplayWorkbench.Updater;

/// <summary>
/// Keeps tools/diffs filled with every opcodediff diff from the chain's floor
/// upward. Copies from a local opcodediff checkout when one is around — that is
/// 2.5 MB of downloads saved on a fresh clone — then fetches whatever is still
/// missing. Only absent files are touched, so the usual run after a hotfix pulls
/// exactly one file.
/// </summary>
public static class DiffMirror
{
    private const string DiffsApi = "https://api.github.com/repos/xivdev/opcodediff/contents/diffs";
    private const string DiffsRaw = "https://raw.githubusercontent.com/xivdev/opcodediff/main/diffs/{0}";
    private const string Suffix = ".diff.json";

    public static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Replay.Workbench-update_patch");
        return http;
    }

    public static List<string> Versions(string mirror)
    {
        if (!Directory.Exists(mirror)) return new List<string>();
        return PatchVersion.Sorted(Directory.EnumerateFiles(mirror, "*" + Suffix)
            .Select(f => Path.GetFileName(f)[..^Suffix.Length]));
    }

    public static List<string> Sync(string mirror, string floor, string? local, Action<string> log, HttpClient http)
    {
        Directory.CreateDirectory(mirror);
        var have = new HashSet<string>(Versions(mirror), StringComparer.Ordinal);

        if (local is not null)
        {
            var copied = 0;
            foreach (var file in Directory.EnumerateFiles(local, "*" + Suffix).OrderBy(f => f, StringComparer.Ordinal))
            {
                var version = Path.GetFileName(file)[..^Suffix.Length];
                if (have.Contains(version) || PatchVersion.Compare(version, floor) < 0) continue;
                File.Copy(file, Path.Combine(mirror, version + Suffix), overwrite: true);
                have.Add(version);
                copied++;
            }
            if (copied > 0) log($"copied {copied} diff(s) from {local}");
        }

        Dictionary<string, string> remote;
        try
        {
            using var doc = JsonDocument.Parse(http.GetByteArrayAsync(DiffsApi).GetAwaiter().GetResult());
            remote = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith(Suffix, StringComparison.Ordinal)) continue;
                var url = entry.TryGetProperty("download_url", out var d) ? d.GetString() : null;
                remote[name[..^Suffix.Length]] = url ?? string.Format(DiffsRaw, name);
            }
        }
        catch (Exception e)
        {
            if (have.Count == 0)
                throw new FatalException($"could not list opcodediff's diffs/ ({e.Message}) and tools/diffs is empty");
            log($"warning: could not reach GitHub ({e.Message}); working from the {have.Count} mirrored diff(s)");
            return Versions(mirror);
        }

        var wanted = remote.Keys.Where(v => PatchVersion.Compare(v, floor) >= 0).ToList();
        var missing = PatchVersion.Sorted(wanted.Where(v => !have.Contains(v)));
        foreach (var version in missing)
        {
            var data = http.GetByteArrayAsync(remote[version]).GetAwaiter().GetResult();
            // a truncated download must not become a silent bad hop
            using (JsonDocument.Parse(data)) { }
            File.WriteAllBytes(Path.Combine(mirror, version + Suffix), data);
            log($"downloaded {version}{Suffix} ({data.Length:N0} bytes)");
        }
        if (missing.Count == 0)
            log($"mirror already has every published diff ({wanted.Count} from {floor} up)");
        return Versions(mirror);
    }
}
