using System.Buffers.Binary;
using System.Text;

namespace ReplayWorkbench.Core;

/// <summary>What a pull export produced, plus what it had to throw away.</summary>
public sealed class PullExport
{
    public required byte[] Bytes { get; init; }
    /// <summary>Stale instance-load duplicate spawns removed from the setup block.</summary>
    public required int GhostsDropped { get; init; }
}

/// <summary>
/// Reconstructs a single pull as a standalone .dat: the zone-in setup block,
/// then the pull's own packets rebased to a zero timeline.  (Port of the
/// validated split_pulls_small.py.)
/// </summary>
public static class PullExporter
{
    /// <summary>
    /// Apply the edited player names in place, size-preserving, at every
    /// occurrence of each original name.
    /// </summary>
    public static void ApplyNameEdits(byte[] target, IEnumerable<PlayerName> players)
    {
        foreach (var p in players)
        {
            if (p.NewName is null || p.NewName == p.Name) continue;
            var nb = Encoding.UTF8.GetBytes(p.NewName);
            if (nb.Length > 31)
                throw new InvalidOperationException($"\"{p.NewName}\" is {nb.Length} bytes (max 31).");
            foreach (var off in p.Offsets)
            {
                Array.Clear(target, off, 32);     // clear the 32-byte field
                nb.CopyTo(target, off);           // write new name
            }
        }
    }

    /// <summary>The whole recording with edited names, nothing split.</summary>
    public static byte[] BuildRenamedFull(ReplayFile file)
    {
        var outp = (byte[])file.Raw.Clone();
        ApplyNameEdits(outp, file.Players);
        return outp;
    }

    public static PullExport BuildPull(ReplayFile file, int pullIdx, ExportOptions opts)
    {
        // source bytes (optionally with name edits applied)
        var src = file.Raw;
        if (opts.ApplyNames)
        {
            src = (byte[])file.Raw.Clone();
            ApplyNameEdits(src, file.Players);
        }

        var segs = file.Segments;
        var p = file.Pulls[pullIdx];
        int pullIndex = p.StartIndex, endIndex = p.EndIndex;
        var pullStartMs = p.Chapter.Ms;

        // setup block: 0 .. director packet inclusive
        var directorIndex = -1;
        for (var i = 0; i < segs.Count; i++)
            if (segs[i].Opcode == ReplayFormat.DirectorOpcode) { directorIndex = i; break; }
        var setupEnd = directorIndex >= 0 ? directorIndex + 1 : file.Pulls[0].StartIndex;

        var carryStart = p.RespawnStart;
        if (carryStart < setupEnd) carryStart = pullIndex;

        var anchorMs = pullStartMs; // timeline zero for the carried range

        // Keep this pull's countdown: the game's type-1 "Countdown" chapter marks
        // the engage (start of the boss fight), which sits inside the pull, just
        // after the Start/Restart. It's already within the carried range, so no
        // boundaries move - we just emit a second chapter entry for it so the
        // exported file exposes the engage as a selectable chapter.
        var cdOn = opts.Countdown && p.CountdownIndex >= pullIndex && p.CountdownIndex < endIndex;
        var countdownIndex = cdOn ? p.CountdownIndex : -1;

        // Instance-load duplicates: the setup block spawns every actor present at
        // zone-in. For pulls after the first, some of those (e.g. the boss's dormant
        // intro copy) are stale - the despawn/cleanup that removes them lives in the
        // gap between setup and the respawn batch, which this reconstruction drops,
        // so carrying their spawn leaves a frozen ghost next to the real, re-spawned
        // actor. Remove a setup NpcSpawn when the actor never appears in this pull
        // AND a live actor of the same model is spawned in the pull (a true duplicate).
        var pullOids = new HashSet<uint>();
        var liveModels = new HashSet<uint>();
        for (var i = carryStart; i < endIndex; i++)
        {
            pullOids.Add(segs[i].Oid);
            if (segs[i].Opcode != file.SpawnOpcode) continue;
            var m = NpcModel(file, segs[i]);
            if (m is not null) liveModels.Add(m.Value);
        }
        var ghostsDropped = 0;

        var parts = new List<byte[]>();

        // 1) setup, original ms (minus stale instance-load duplicates)
        for (var i = 0; i < setupEnd; i++)
        {
            var s = segs[i];
            if (s.Opcode == file.SpawnOpcode && !pullOids.Contains(s.Oid))
            {
                var m = NpcModel(file, s);
                if (m is not null && liveModels.Contains(m.Value)) { ghostsDropped++; continue; }
            }
            parts.Add(SegRaw(src, s));
        }

        // 2+3) [countdown/respawn .. next pull], rebased; inject waymarks at the pull start
        int chapterNewOffset = -1, countdownNewOffset = -1;
        for (var i = carryStart; i < endIndex; i++)
        {
            if (i == countdownIndex) countdownNewOffset = ByteLen(parts);
            if (i == pullIndex)
            {
                // the chapter points at the pull start; the waymark packets are
                // emitted here at ms=0, right before the pull's own first packet -
                // same as the validated Python splitter
                chapterNewOffset = ByteLen(parts);
                if (opts.Waymarks) InjectWaymarks(file, src, parts, pullIndex);
            }
            parts.Add(RebasedSeg(src, segs[i], (long)segs[i].Ms - anchorMs));
        }
        if (chapterNewOffset < 0) chapterNewOffset = ByteLen(parts);

        var body = Concat(parts);
        var lastMs = endIndex > carryStart && segs[endIndex - 1].Ms > anchorMs
            ? segs[endIndex - 1].Ms - anchorMs
            : 0u;

        // header
        var header = src[..ReplayFormat.HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(ReplayFormat.OffReplayLen), body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(ReplayFormat.OffTotalMs), lastMs);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(ReplayFormat.OffDisplayedMs), lastMs);

        // chapter array: the pull start, then the countdown/engage (if kept).
        // Chapters are ascending: the Start/Restart first, the engage a little later.
        var ca = new byte[ReplayFormat.ChapterArray];
        var cav = ca.AsSpan();
        if (cdOn && countdownNewOffset >= 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(cav, 2);
            BinaryPrimitives.WriteInt32LittleEndian(cav[4..], p.Chapter.Type);            // chapter[0] = start/restart
            BinaryPrimitives.WriteUInt32LittleEndian(cav[8..], (uint)chapterNewOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(cav[12..], 0);                        // pullStartMs - anchorMs
            const int e = ReplayFormat.ChapterEntry;
            BinaryPrimitives.WriteInt32LittleEndian(cav[(4 + e)..], p.Countdown!.Type);    // chapter[1] = countdown/engage
            BinaryPrimitives.WriteUInt32LittleEndian(cav[(8 + e)..], (uint)countdownNewOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(cav[(12 + e)..],
                p.Countdown.Ms > anchorMs ? p.Countdown.Ms - anchorMs : 0);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(cav, 1);
            BinaryPrimitives.WriteInt32LittleEndian(cav[4..], p.Chapter.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(cav[8..], (uint)chapterNewOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(cav[12..], 0);
        }

        return new PullExport
        {
            Bytes = Concat(new List<byte[]> { header, ca, body }),
            GhostsDropped = ghostsDropped,
        };
    }

    /// <summary>
    /// Carry the last waymarks placed before the pull into it: a non-empty preset
    /// if one was captured, otherwise the latest placement of each individual marker.
    /// </summary>
    private static void InjectWaymarks(ReplayFile file, byte[] src, List<byte[]> parts, int pullIndex)
    {
        var latestIndividual = new Dictionary<byte, Segment>();
        Segment? latestPreset = null;
        for (var j = 0; j < pullIndex; j++)
        {
            var sj = file.Segments[j];
            if (sj.Opcode == file.WaymarkOpcode)
                latestIndividual[file.Raw[file.SegPayload(sj)]] = sj;   // marker id is the first payload byte
            else if (sj.Opcode == file.WaymarkPresetOpcode && !file.IsEmptyPreset(sj))
                latestPreset = sj;
        }

        if (latestPreset is not null)
        {
            parts.Add(RebasedSeg(src, latestPreset, 0));
            return;
        }
        foreach (var k in latestIndividual.Keys.OrderBy(x => x))
            parts.Add(RebasedSeg(src, latestIndividual[k], 0));
    }

    /// <summary>The NpcSpawn payload's model id, or null when the payload is too short.</summary>
    private static uint? NpcModel(ReplayFile file, Segment s) =>
        s.DataLength >= 0x48 ? file.U32(file.SegPayload(s) + 0x44) : null;

    private static byte[] SegRaw(byte[] src, Segment s)
    {
        var b = ReplayFormat.DataStart + s.Offset;
        return src[b..(b + s.Total)];
    }

    private static byte[] RebasedSeg(byte[] src, Segment s, long newMs)
    {
        var b = ReplayFormat.DataStart + s.Offset;
        var outp = src[b..(b + s.Total)];
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(4), (uint)Math.Max(0, newMs));
        return outp;
    }

    private static int ByteLen(List<byte[]> parts)
    {
        var n = 0;
        foreach (var p in parts) n += p.Length;
        return n;
    }

    private static byte[] Concat(List<byte[]> parts)
    {
        var outp = new byte[ByteLen(parts)];
        var o = 0;
        foreach (var p in parts) { p.CopyTo(outp, o); o += p.Length; }
        return outp;
    }
}
