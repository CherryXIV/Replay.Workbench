using System.Text.Json;

namespace ReplayWorkbench.Updater;

/// <summary>
/// One <c>&lt;patch&gt;.diff.json</c> read into a previous-patch → this-patch opcode map.
///
/// <para>An entry pairs one old opcode with one new one when the diff resolved it.
/// Everything else is attrition worth reporting rather than guessing at: n:n
/// groups (n &gt; 1) are candidates the matcher could not tell apart, an entry with
/// no "new" is an opcode that went away, and one with no "old" is an opcode that
/// appeared this patch. The 6.3 diff spells the unresolved case as
/// "candidates"/"unknown" keys; both shapes fall out of the same length checks.</para>
/// </summary>
public sealed class DiffHop
{
    public required string Version { get; init; }
    public required Dictionary<int, int> Map { get; init; }
    public required HashSet<int> Ambiguous { get; init; }
    public required HashSet<int> Removed { get; init; }
    /// <summary>Targets claimed by more than one source, and the sources that claimed them.</summary>
    public required Dictionary<int, List<int>> Collisions { get; init; }

    /// <summary>Every opcode the previous patch is known to have used.</summary>
    public HashSet<int> Known
    {
        get
        {
            var all = new HashSet<int>(Map.Keys);
            all.UnionWith(Ambiguous);
            all.UnionWith(Removed);
            return all;
        }
    }

    public static string PathFor(string diffsDir, string version) =>
        Path.Combine(diffsDir, $"{version}.diff.json");

    public static DiffHop Load(string diffsDir, string version)
    {
        var path = PathFor(diffsDir, version);
        if (!File.Exists(path)) throw new FatalException($"No diff for {version} (expected {path})");

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var map = new Dictionary<int, int>();
        var ambiguous = new HashSet<int>();
        var removed = new HashSet<int>();

        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var olds = HexList(e, "old");
            var news = HexList(e, "new");
            if (olds.Count == 1 && news.Count == 1) map[olds[0]] = news[0];
            else if (olds.Count == 0) continue;      // brand new opcode; nothing to carry forward
            else if (news.Count == 0) removed.UnionWith(olds);
            else ambiguous.UnionWith(olds);
        }

        // Two sources landing on one target would collapse two packet types onto a
        // single opcode — the client reads one with the other's struct and crashes.
        // Demote both sides rather than pick one.
        var byTarget = new Dictionary<int, List<int>>();
        foreach (var (old, @new) in map)
        {
            if (!byTarget.TryGetValue(@new, out var l)) byTarget[@new] = l = new List<int>();
            l.Add(old);
        }
        var collisions = new Dictionary<int, List<int>>();
        foreach (var (target, sources) in byTarget)
        {
            if (sources.Count <= 1) continue;
            // Python builds this dict while iterating a dict keyed by old opcode, so
            // the source order follows insertion; sort for a stable report either way.
            sources.Sort();
            collisions[target] = sources;
            foreach (var old in sources)
            {
                map.Remove(old);
                ambiguous.Add(old);
            }
        }

        return new DiffHop
        {
            Version = version,
            Map = map,
            Ambiguous = ambiguous,
            Removed = removed,
            Collisions = collisions,
        };

        static List<int> HexList(JsonElement e, string name)
        {
            var outp = new List<int>();
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return outp;
            foreach (var item in v.EnumerateArray())
            {
                var s = item.GetString();
                if (s is not null) outp.Add(Convert.ToInt32(s, 16));
            }
            return outp;
        }
    }

    /// <summary>
    /// Does this hop actually start where the previous patch ended?
    ///
    /// <para>The chain is ordered by version name, which is a guess about release
    /// order. A hop whose old side is the previous patch's new side confirms the
    /// guess; one that does not means the diffs arrived out of order and the chain
    /// would carry every recording onto the wrong packets.</para>
    /// </summary>
    public static string Alignment(string diffsDir, string previous, string version)
    {
        var hop = Load(diffsDir, version);
        var prev = Load(diffsDir, previous);
        var prevNews = new HashSet<int>(prev.Map.Values);
        if (prevNews.Count == 0) return "previous hop is empty; cannot check alignment";
        var known = hop.Known;
        var overlap = prevNews.Count(known.Contains);
        var pct = 100.0 * overlap / prevNews.Count;
        var verdict = pct >= 95.0 ? "lines up with" : "DOES NOT line up with";
        return $"{overlap}/{prevNews.Count} opcodes ({pct:F1}%) {verdict} {previous}";
    }
}
