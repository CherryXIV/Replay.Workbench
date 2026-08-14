namespace ReplayWorkbench.Core;

/// <summary>
/// Turns the raw per-patch opcode diffs into the two questions the tools ask:
///
///   <see cref="ChainMap"/>   how do this patch's opcodes reach that patch's?
///   <see cref="PatchTable"/> what was each IPC name's opcode back then?
///
/// The first is what transpose runs on, and it needs no names at all.  The
/// second exists only for labels and for the few packets looked up by name; it
/// works by carrying the latest patch's names <i>backwards</i> down the same
/// chain, so exactly one hand-maintained name table is needed no matter how far
/// back a recording goes.
/// </summary>
public static class PatchChain
{
    private static readonly Dictionary<string, PatchHop?> HopCache = new();
    private static readonly Dictionary<string, HashSet<int>?> UniverseCache = new();
    private static readonly Dictionary<string, PatchChainMap?> ChainCache = new();
    private static readonly Dictionary<string, IReadOnlyDictionary<string, int>?> TableCache = new();

    static PatchChain()
    {
        // Registering a table at runtime changes what "latest" means, which
        // changes every derived name table. The diff-based caches are immutable
        // data and survive.
        OpcodeData.Changed += () => { lock (TableCache) TableCache.Clear(); };
    }

    /// <summary>Decode one hop (the move from the previous patch to <paramref name="version"/>).</summary>
    public static PatchHop? Hop(string version)
    {
        lock (HopCache)
        {
            if (HopCache.TryGetValue(version, out var cached)) return cached;
            PatchHop? hop = null;
            var packed = OpcodeData.PackedDiff(version);
            if (packed is not null)
            {
                var o = OpcodeData.UnpackOpcodes(packed.O);
                var n = OpcodeData.UnpackOpcodes(packed.N);
                var map = new Dictionary<int, int>(o.Length);
                for (var i = 0; i < o.Length && i < n.Length; i++) map[o[i]] = n[i];
                // `lost` is everything the diff saw on the old side but could not
                // carry forward: candidates it couldn't tell apart (a), plus
                // opcodes the patch deleted (r).
                var lost = new HashSet<int>(OpcodeData.UnpackOpcodes(packed.A));
                lost.UnionWith(OpcodeData.UnpackOpcodes(packed.R));
                hop = new PatchHop { Version = version, Map = map, Lost = lost };
            }
            HopCache[version] = hop;
            return hop;
        }
    }

    /// <summary>Every opcode a patch is known to use: the old side of the hop that leaves it.</summary>
    public static IReadOnlySet<int>? Universe(string patch)
    {
        lock (UniverseCache)
        {
            if (UniverseCache.TryGetValue(patch, out var cached)) return cached;
            HashSet<int>? set = null;
            if (OpcodeData.InChain(patch))
            {
                var i = OpcodeData.ChainPos(patch);
                var next = i + 1 < OpcodeData.Chain.Count ? Hop(OpcodeData.Chain[i + 1]) : null;
                if (next is not null)
                {
                    set = new HashSet<int>(next.Map.Keys);
                    set.UnionWith(next.Lost);
                }
                else
                {
                    var own = Hop(patch);
                    if (own is not null) set = new HashSet<int>(own.Map.Values);
                }
            }
            UniverseCache[patch] = set;
            return set;
        }
    }

    /// <summary>
    /// Compose the hops from <paramref name="from"/> up to <paramref name="to"/>,
    /// one patch at a time, over every opcode <paramref name="from"/> is known to
    /// have used.  <c>Lost</c> says which patch dropped each opcode and why - an
    /// opcode that falls out mid-chain cannot be carried the rest of the way, and
    /// pretending otherwise is how you ship a replay that crashes the client.
    /// </summary>
    public static PatchChainMap? ChainMap(string from, string to)
    {
        if (!OpcodeData.InChain(from) || !OpcodeData.InChain(to)) return null;
        int i = OpcodeData.ChainPos(from), j = OpcodeData.ChainPos(to);
        if (j < i) return null;

        var key = from + ">" + to;
        lock (ChainCache)
        {
            if (ChainCache.TryGetValue(key, out var cached)) return cached;

            var map = new Dictionary<int, int>();
            var lost = new Dictionary<int, string>();
            if (j > i)
            {
                var first = Hop(OpcodeData.Chain[i + 1]);
                if (first is null) { ChainCache[key] = null; return null; }
                foreach (var op in first.Map.Keys) map[op] = op;
                foreach (var op in first.Lost) map[op] = op;

                for (var k = i + 1; k <= j; k++)
                {
                    var hop = Hop(OpcodeData.Chain[k]);
                    if (hop is null) { ChainCache[key] = null; return null; }
                    foreach (var orig in map.Keys.ToList())
                    {
                        var cur = map[orig];
                        if (hop.Map.TryGetValue(cur, out var next)) map[orig] = next;
                        else
                        {
                            map.Remove(orig);
                            lost[orig] = hop.Lost.Contains(cur)
                                ? $"dropped in {hop.Version}"
                                : $"absent from the {hop.Version} diff";
                        }
                    }
                }
            }

            var result = new PatchChainMap { Map = map, Lost = lost };
            ChainCache[key] = result;
            return result;
        }
    }

    /// <summary>
    /// Which patch was this recording made on?  Ask the file, not the build number.
    ///
    /// <para>Every patch reshuffles the whole IPC vtable, so a recording's set of
    /// opcodes only fits the patch it was actually made on: score each candidate by
    /// how many of the file's packets its vtable accounts for and can carry all the
    /// way to <paramref name="to"/>, and the right patch comes out at 100% while its
    /// neighbours sit well below.  That matters because the alternative - a
    /// hand-maintained build number table - is exactly the kind of thing that goes
    /// wrong quietly: guess the patch one hotfix off and every opcode still remaps,
    /// just to the wrong packet.</para>
    /// </summary>
    public static PatchDetection? DetectPatch(IReadOnlyDictionary<int, int> hist, string? to = null)
    {
        to ??= OpcodeData.LatestPatch;
        if (!OpcodeData.InChain(to)) return null;

        long total = 0;
        var kindTotal = 0;
        foreach (var (op, n) in hist)
        {
            if (op >= ReplayFormat.IpcOpcodeCeiling) continue;
            total += n;
            kindTotal++;
        }
        if (total == 0) return null;

        var scores = new List<(string Patch, double Packets, double Kinds)>();
        foreach (var from in OpcodeData.Chain)
        {
            if (OpcodeData.ChainPos(from) > OpcodeData.ChainPos(to)) continue;
            var uni = Universe(from);
            var chain = from == to ? null : ChainMap(from, to);
            if (uni is null || (from != to && chain is null)) continue;

            long packets = 0;
            var kinds = 0;
            foreach (var (op, n) in hist)
            {
                if (op >= ReplayFormat.IpcOpcodeCeiling) continue;
                // in this patch's vtable, and still standing at the far end of the chain
                var accounted = from == to ? uni.Contains(op) : chain!.Map.ContainsKey(op);
                if (!accounted) continue;
                packets += n;
                kinds++;
            }
            scores.Add((from, (double)packets / total, (double)kinds / kindTotal));
        }
        if (scores.Count == 0) return null;

        scores.Sort((a, b) =>
        {
            var c = b.Packets.CompareTo(a.Packets);
            return c != 0 ? c : b.Kinds.CompareTo(a.Kinds);
        });

        var best = scores[0];
        var next = scores.Count > 1 ? scores[1] : default;
        var hasNext = scores.Count > 1;
        // Score on opcode *kinds*, not packet share: one chatty opcode (ActorMove is
        // half the file) pins the packet share near 100% for several patches, while
        // the count of opcodes a patch can account for separates them cleanly.
        var margin = hasNext ? best.Kinds - next.Kinds : 1.0;
        return new PatchDetection(
            best.Patch, best.Packets, best.Kinds,
            hasNext ? next.Patch : null, margin,
            // Only worth acting on when the fit is exact and nothing else comes close.
            best.Packets >= 0.9999 && best.Kinds >= 0.9999 && margin > 0.01);
    }

    /// <summary>
    /// IPC names for a patch.  The latest patch's table is pasted in by hand (and
    /// is the one that gets hand-corrected); everything older is that table's
    /// names carried backwards down the chain.  A pasted table wins where one
    /// exists, so tables already verified against real recordings keep behaving
    /// exactly as they did.
    /// </summary>
    public static IReadOnlyDictionary<string, int>? PatchTable(string? patch)
    {
        if (patch is null) return null;
        var pasted = OpcodeData.RawTable(patch);
        if (OpcodeData.HasNames(pasted)) return pasted;

        lock (TableCache)
        {
            if (TableCache.TryGetValue(patch, out var cached)) return cached;

            Dictionary<string, int>? outp = null;
            var latest = OpcodeData.RawTable(OpcodeData.LatestPatch);
            var chain = OpcodeData.HasNames(latest)
                        && OpcodeData.InChain(patch)
                        && OpcodeData.InChain(OpcodeData.LatestPatch)
                ? ChainMap(patch, OpcodeData.LatestPatch)
                : null;
            if (chain is not null)
            {
                var back = new Dictionary<int, int>(chain.Map.Count);
                foreach (var (was, now) in chain.Map) back[now] = was;
                outp = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (name, op) in latest!)
                    if (back.TryGetValue(op, out var here)) outp[name] = here;
            }
            TableCache[patch] = outp;
            return outp;
        }
    }

    /// <summary>Look one packet up by name in a patch's table.</summary>
    public static int? Lookup(string? patch, string name)
    {
        var t = PatchTable(patch);
        return t is not null && t.TryGetValue(name, out var op) ? op : null;
    }
}
