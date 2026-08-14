using System.Buffers.Binary;

namespace ReplayWorkbench.Core;

/// <summary>Rewriting a finished export's opcodes from one patch onto another.</summary>
public static class Transpose
{
    /// <summary>
    /// IPC names in <paramref name="table"/> that share one opcode.  Transpose maps
    /// packets by name, so a duplicated opcode value collapses two packet types
    /// into one: the client parses one of them with the other's struct and crashes.
    /// (A 3672-byte PartyList arriving on PlayerSpawn's opcode is how this bit us
    /// before.)  Both registration and transpose refuse such a table rather than
    /// write a replay that takes the game down.
    /// </summary>
    public static IReadOnlyList<(int Opcode, IReadOnlyList<string> Names)> OpcodeCollisions(
        IReadOnlyDictionary<string, int> table)
    {
        var byOp = new Dictionary<int, List<string>>();
        foreach (var (name, op) in table)
        {
            if (!byOp.TryGetValue(op, out var names)) byOp[op] = names = new List<string>();
            names.Add(name);
        }
        return byOp.Where(kv => kv.Value.Count > 1)
                   .Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value))
                   .ToList();
    }

    public static string DescribeCollisions(
        IReadOnlyList<(int Opcode, IReadOnlyList<string> Names)> cols, int limit = 2)
    {
        var head = string.Join("; ", cols.Take(limit).Select(c => $"{c.Opcode} = {string.Join(" + ", c.Names)}"));
        return cols.Count > limit ? $"{head}; +{cols.Count - limit} more" : head;
    }

    /// <summary>
    /// How to get from one patch to another.  The diff chain is the real answer;
    /// the name tables are a stopgap for the one case the chain can't cover - a
    /// brand new patch registered at runtime, which has names published but no
    /// diff yet.
    /// </summary>
    public static RemapPlan Plan(string from, string to)
    {
        var chain = PatchChain.ChainMap(from, to);
        if (chain is not null)
            return new RemapPlan { Ok = true, Via = "diffs", Map = chain.Map, Lost = chain.Lost };

        var fromTable = OpcodeData.RawTable(from);
        var toTable = OpcodeData.RawTable(to);
        if (!OpcodeData.HasNames(fromTable) || !OpcodeData.HasNames(toTable))
            return RemapPlan.Fail($"no diff and no opcode table linking {from} to {to}");

        // Remapping by name onto a table with a duplicated opcode collapses two
        // packet types into one and the client crashes reading one as the other.
        // Refuse before touching a byte - skipping the transpose is recoverable,
        // shipping that isn't.
        foreach (var (patch, table, label) in new[]
                 { (to, toTable!, "target"), (from, fromTable!, "source") })
        {
            var cols = OpcodeCollisions(table);
            if (cols.Count > 0)
                return RemapPlan.Fail(
                    $"the {label} table ({patch}) gives one opcode two packet names " +
                    $"({DescribeCollisions(cols)}) - remapping onto it would crash the game; fix the table first");
        }

        var map = new Dictionary<int, int>();
        var lost = new Dictionary<int, string>();
        foreach (var (name, op) in fromTable!)
        {
            if (toTable!.TryGetValue(name, out var target)) map[op] = target;
            else lost[op] = $"{name} has no entry in {to}";
        }
        return new RemapPlan { Ok = true, Via = "names", Map = map, Lost = lost };
    }

    /// <summary>
    /// Rewrite every segment opcode in a finished export buffer from
    /// <paramref name="filePatch"/> to the latest patch, in place.  Returns
    /// coverage info so the UI can be honest about how complete the remap is.
    /// </summary>
    public static TransposeResult Apply(byte[] bytes, string? filePatch, int fileBuild)
    {
        if (filePatch is null) return TransposeResult.Fail($"no patch known for build {fileBuild}");
        var latest = OpcodeData.LatestPatch;
        if (filePatch == latest) return TransposeResult.Fail("already on the latest patch");

        var plan = Plan(filePatch, latest);
        if (!plan.Ok) return TransposeResult.Fail(plan.Reason!);

        var span = bytes.AsSpan();
        var replayLen = BinaryPrimitives.ReadInt32LittleEndian(span[ReplayFormat.OffReplayLen..]);

        // First pass: what's actually in the file. Opcodes at 0xf000 and up are
        // replay control markers, not IPC, and are left alone.
        var hist = new Dictionary<int, int>();
        int off = 0, segTotal = 0;
        while (off < replayLen)
        {
            var b = ReplayFormat.DataStart + off;
            var op = BinaryPrimitives.ReadUInt16LittleEndian(span[b..]);
            if (op < ReplayFormat.IpcOpcodeCeiling) hist[op] = hist.GetValueOrDefault(op) + 1;
            segTotal++;
            off += ReplayFormat.SegHeader + BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
        }

        // An opcode we can't map keeps its old number. That's survivable on its own,
        // but if some *other* packet has since moved onto that number, the client
        // reads the leftovers with the wrong struct and dies. Check before writing.
        var targets = new HashSet<int>();
        foreach (var op in hist.Keys)
            if (plan.Map.TryGetValue(op, out var t)) targets.Add(t);
        var stale = hist.Keys.Where(op => !plan.Map.ContainsKey(op) && targets.Contains(op)).ToList();
        if (stale.Count > 0)
            return TransposeResult.Fail(
                $"{stale.Count} packet type(s) can't be remapped " +
                $"({string.Join(", ", stale.Take(3).Select(o => "0x" + o.ToString("x")))}{(stale.Count > 3 ? ", …" : "")}) " +
                "and another packet has moved onto their opcodes - the export would crash the client");

        int rewritten = 0, unknownSegs = 0;
        var unknownKinds = new HashSet<int>();
        off = 0;
        while (off < replayLen)
        {
            var b = ReplayFormat.DataStart + off;
            var op = BinaryPrimitives.ReadUInt16LittleEndian(span[b..]);
            var len = BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            if (plan.Map.TryGetValue(op, out var to))
            {
                if (to != op)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(span[b..], (ushort)to);
                    rewritten++;
                }
            }
            else if (op < ReplayFormat.IpcOpcodeCeiling)
            {
                unknownSegs++;
                unknownKinds.Add(op);
            }
            off += ReplayFormat.SegHeader + len;
        }

        return new TransposeResult
        {
            Ok = true,
            From = filePatch,
            To = latest,
            Via = plan.Via,
            Rewritten = rewritten,
            SegTotal = segTotal,
            UnknownSegs = unknownSegs,
            UnknownKinds = unknownKinds.Count,
        };
    }

    /// <summary>
    /// Remap to the latest patch and stamp the latest build.  Remap first, stamp
    /// second: a file stamped to the latest build but still carrying its old
    /// opcodes is the one combination that loads and then crashes, so the build
    /// only moves once the packets actually did.
    /// </summary>
    /// <returns>A status fragment for the log, empty when nothing was done.</returns>
    public static string ApplyAndStamp(byte[] bytes, string? filePatch, int fileBuild)
    {
        var r = Apply(bytes, filePatch, fileBuild);
        if (!r.Ok) return $" (transpose skipped: {r.Reason})";
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(ReplayFormat.OffBuild), OpcodeData.LatestGameBuild);
        var s = $" · {r.From}→{r.To} via {r.Via}: {r.Rewritten}/{r.SegTotal} packets remapped";
        if (r.UnknownSegs > 0) s += $", {r.UnknownSegs} unmapped";
        return s;
    }
}
