using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ReplayWorkbench.Updater;

/// <summary>
/// tools/replay_builds.json — the build number → patch hint bump_replay.py reads.
/// Rewritten in build order, preserving the file's leading _comment block.
/// </summary>
public static class BuildsJson
{
    public static void Add(string path, int build, string patch, Action<string> log)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;

        var builds = new SortedDictionary<int, string>();
        if (root.TryGetProperty("builds", out var existing))
            foreach (var e in existing.EnumerateObject())
                builds[int.Parse(e.Name)] = e.Value.GetString() ?? "";

        if (builds.TryGetValue(build, out var already) && already == patch)
        {
            log("tools/replay_builds.json already has that build");
            return;
        }
        builds[build] = patch;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = true, IndentCharacter = ' ', IndentSize = 2,
                   // match Python's json.dumps, which leaves > < & ' + alone
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            w.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("builds")) continue;
                prop.WriteTo(w);
            }
            w.WritePropertyName("builds");
            w.WriteStartObject();
            foreach (var (b, p) in builds) w.WriteString(b.ToString(), p);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n";
        ChainFile.WriteText(path, text);
        log($"tools/replay_builds.json: +{build} -> {patch}");
    }
}
