using System.Buffers.Binary;
using System.Text;

namespace ReplayWorkbench.Core;

/// <summary>
/// A loaded FFXIVReplay .dat: its header, segments, chapters, the pulls derived
/// from them, and the character names found in the bytes.
/// </summary>
public sealed class ReplayFile
{
    public byte[] Raw { get; }
    public string FileName { get; }

    public IReadOnlyList<Segment> Segments { get; }
    public IReadOnlyList<Chapter> Chapters { get; }
    public IReadOnlyList<Pull> Pulls { get; }
    public IReadOnlyList<PlayerName> Players { get; }

    public int FileBuild { get; }
    /// <summary>The patch this file's packets are in, or null when unidentified.</summary>
    public string? FilePatch { get; }
    /// <summary>What the file's own opcodes say, regardless of what won.</summary>
    public PatchDetection? PatchDetected { get; }
    /// <summary>The patch the user picked by hand, if any.</summary>
    public string? PatchOverride { get; }

    // Resolved per the file's patch so the tool reads old recordings correctly.
    public int SpawnOpcode { get; }
    public int WaymarkOpcode { get; }
    public int WaymarkPresetOpcode { get; }
    /// <summary>FirstAttack fires on the first hit against the boss - the real engage.</summary>
    public int FirstAttackOpcode { get; }
    /// <summary>Casts/effects: their last one ends combat, their first is the fallback start.</summary>
    public IReadOnlySet<int> CombatOps { get; }

    /// <summary>Opcode to how many segments carry it.</summary>
    public IReadOnlyDictionary<int, int> Histogram { get; }

    /// <summary>
    /// The payload length of this file's PlayerSpawn packets, when it is a size no
    /// <see cref="SpawnLayout"/> covers - otherwise null.
    ///
    /// <para>This is the failure worth saying out loud.  A patch that moves the
    /// spawn fields again lands here, and everything that reads a character goes
    /// quiet at once: no roster, an empty appearance editor, and an anonymize pass
    /// that reports success while leaving every real name in the file.  Silence
    /// looks like "this recording has no players", so the caller should say what
    /// actually happened instead.</para>
    /// </summary>
    public int? UnknownSpawnLength { get; }

    private ReplayFile(byte[] raw, string fileName, string? patchOverride)
    {
        Raw = raw;
        FileName = fileName;
        PatchOverride = patchOverride;

        for (var i = 0; i < ReplayFormat.Magic.Length; i++)
            if (i >= raw.Length || raw[i] != ReplayFormat.Magic[i])
                throw new InvalidDataException("Not an FFXIVREPLAY .dat (bad header).");
        if (raw.Length < ReplayFormat.DataStart)
            throw new InvalidDataException("File is too short to hold a replay header.");

        var replayLength = I32(ReplayFormat.OffReplayLen);
        if (replayLength < 0 || ReplayFormat.DataStart + (long)replayLength > raw.Length)
            throw new InvalidDataException(
                $"Replay length field ({replayLength:N0} bytes) runs past the end of the file - it is truncated or corrupt.");

        // walk segments
        var segs = new List<Segment>();
        var hist = new Dictionary<int, int>();
        var off = 0;
        while (off < replayLength)
        {
            var b = ReplayFormat.DataStart + off;
            if (b + ReplayFormat.SegHeader > raw.Length) break;
            var opcode = U16(b);
            var dataLength = U16(b + 2);
            segs.Add(new Segment
            {
                Offset = off,
                Opcode = opcode,
                DataLength = dataLength,
                Ms = U32(b + 4),
                Oid = U32(b + 8),
            });
            hist[opcode] = hist.GetValueOrDefault(opcode) + 1;
            off += ReplayFormat.SegHeader + dataLength;
        }
        Segments = segs;
        Histogram = hist;

        // Which patch this is has to wait for the segment walk: the answer comes
        // from the opcodes themselves, with the build number as a fallback.
        // Everything below reads packets by name, so it runs after.
        FileBuild = I32(ReplayFormat.OffBuild);
        PatchDetected = PatchChain.DetectPatch(hist);
        FilePatch = DecidePatch(FileBuild, patchOverride, PatchDetected);

        var table = PatchChain.PatchTable(FilePatch);
        SpawnOpcode = table?.GetValueOrDefault("NpcSpawn", ReplayFormat.DefaultSpawnOpcode)
                      ?? ReplayFormat.DefaultSpawnOpcode;
        WaymarkOpcode = table?.GetValueOrDefault("PlaceFieldMarker", ReplayFormat.DefaultWaymarkOpcode)
                        ?? ReplayFormat.DefaultWaymarkOpcode;
        WaymarkPresetOpcode = table?.GetValueOrDefault("PlaceFieldMarkerPreset", ReplayFormat.DefaultWaymarkPresetOpcode)
                              ?? ReplayFormat.DefaultWaymarkPresetOpcode;
        var combat = new HashSet<int>();
        foreach (var name in ReplayFormat.CombatOpNames)
            if (table is not null && table.TryGetValue(name, out var op)) combat.Add(op);
        CombatOps = combat;
        FirstAttackOpcode = table is not null && table.TryGetValue("FirstAttack", out var fa) ? fa : 0;

        // chapters
        var chapters = new List<Chapter>();
        // The count lives in the file and a corrupt one would read straight into
        // the data area, so it is clamped to the array the format actually has.
        var clen = Math.Clamp(I32(ReplayFormat.HeaderSize), 0, ReplayFormat.MaxChapters);
        for (var i = 0; i < clen; i++)
        {
            var e = ReplayFormat.HeaderSize + 4 + i * ReplayFormat.ChapterEntry;
            chapters.Add(new Chapter { Type = I32(e), Offset = U32(e + 4), Ms = U32(e + 8) });
        }
        Chapters = chapters;

        UnknownSpawnLength = FindUnknownSpawnLength();
        Pulls = BuildPulls();
        Players = FindPlayers(replayLength);
    }

    /// <summary>
    /// The size of this file's PlayerSpawn packets when no layout matches it.  Any
    /// one packet we can read means the file is fine, so this only answers when
    /// none of them resolve.
    /// </summary>
    private int? FindUnknownSpawnLength()
    {
        var spawnOp = PatchChain.Lookup(FilePatch, "PlayerSpawn");
        if (spawnOp is null) return null;
        int? unknown = null;
        foreach (var seg in Segments)
        {
            if (seg.Opcode != spawnOp) continue;
            if (CharacterLayout.SpawnLayoutFor(seg.DataLength) is not null) return null;
            unknown ??= seg.DataLength;
        }
        return unknown;
    }

    public static ReplayFile Parse(byte[] bytes, string fileName, string? patchOverride = null) =>
        new(bytes, fileName, patchOverride);

    /// <summary>
    /// Which patch a file is on, most trustworthy source first.
    ///
    /// <para>The file's opcodes beat the build table because that table is typed in
    /// by hand and a wrong entry does not fail loudly: every packet still gets
    /// remapped, just onto the wrong packet type.  Detection is only allowed to win
    /// when it accounts for the file exactly and no other patch comes close.</para>
    /// </summary>
    private static string? DecidePatch(int build, string? patchOverride, PatchDetection? detected)
    {
        if (!string.IsNullOrEmpty(patchOverride)) return patchOverride;
        if (detected is { Confident: true }) return detected.Patch;
        return OpcodeData.BuildToPatch.GetValueOrDefault(build);
    }

    // ---- byte helpers (little-endian, like the game) ----
    public ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(o));
    public uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(o));
    public int I32(int o) => BinaryPrimitives.ReadInt32LittleEndian(Raw.AsSpan(o));
    public ulong U64(int o) => BinaryPrimitives.ReadUInt64LittleEndian(Raw.AsSpan(o));

    public int ReplayLength => I32(ReplayFormat.OffReplayLen);
    public uint TotalMs => U32(ReplayFormat.OffTotalMs);
    public int RecorderIndex => Raw[ReplayFormat.OffPlayerIndex];

    /// <summary>Absolute byte offset of a segment's header.</summary>
    public int SegBase(Segment s) => ReplayFormat.DataStart + s.Offset;

    /// <summary>Absolute byte offset of a segment's payload.</summary>
    public int SegPayload(Segment s) => SegBase(s) + ReplayFormat.SegHeader;

    private List<Pull> BuildPulls()
    {
        var o2i = new Dictionary<int, int>(Segments.Count);
        for (var i = 0; i < Segments.Count; i++) o2i[Segments[i].Offset] = i;

        var pullChapters = Chapters.Where(c => ReplayFormat.PullStartTypes.Contains(c.Type)).ToList();
        var pulls = new List<Pull>(pullChapters.Count);
        for (var n = 0; n < pullChapters.Count; n++)
        {
            var pc = pullChapters[n];
            if (!o2i.TryGetValue((int)pc.Offset, out var startIndex)) continue;
            var endIndex = n < pullChapters.Count - 1
                ? o2i.GetValueOrDefault((int)pullChapters[n + 1].Offset, Segments.Count)
                : Segments.Count;
            var lastMs = endIndex > startIndex ? Segments[endIndex - 1].Ms : pc.Ms;
            var respawnStart = FindRespawnBatchStart(startIndex);
            var batchCount = CountSpawns(respawnStart, startIndex);
            // Cap combat at the wipe: when the party dies the arena resets (mass
            // despawn then re-spawn for the next attempt). Post-wipe DoT ticks and
            // the reset's own spawn effects keep firing for several seconds after -
            // and run almost to the restart - so combat ends at that reset (the next
            // pull's respawn batch).
            var combatEnd = n < pullChapters.Count - 1 ? FindRespawnBatchStart(endIndex) : endIndex;
            var combatMs = CombatSpan(startIndex, combatEnd);
            var nextMs = n < pullChapters.Count - 1 ? pullChapters[n + 1].Ms : uint.MaxValue;

            var countdown = FindCountdownChapter(pc, nextMs);
            var countdownIndex = countdown is not null && o2i.TryGetValue((int)countdown.Offset, out var ci) ? ci : -1;
            if (countdownIndex < 0) countdown = null; // no segment to anchor to -> can't keep it

            pulls.Add(new Pull
            {
                Chapter = pc,
                Number = pulls.Count + 1,
                StartIndex = startIndex,
                EndIndex = endIndex,
                LengthMs = lastMs > pc.Ms ? lastMs - pc.Ms : 0,
                RespawnStart = respawnStart,
                BatchCount = batchCount,
                CombatMs = combatMs,
                Countdown = countdown,
                CountdownIndex = countdownIndex,
            });
        }
        return pulls;
    }

    private int FindRespawnBatchStart(int pullIndex)
    {
        var lo = Math.Max(0, pullIndex - ReplayFormat.BatchLookback);
        var spawns = new List<int>();
        for (var i = lo; i < pullIndex; i++)
            if (Segments[i].Opcode == SpawnOpcode) spawns.Add(i);
        if (spawns.Count == 0) return pullIndex;

        var clusters = new List<List<int>>();
        var cur = new List<int> { spawns[0] };
        for (var k = 1; k < spawns.Count; k++)
        {
            var i = spawns[k];
            if (Segments[i].Ms - Segments[cur[^1]].Ms <= ReplayFormat.BatchMsWindow) cur.Add(i);
            else { clusters.Add(cur); cur = new List<int> { i }; }
        }
        clusters.Add(cur);

        List<int>? chosen = null;
        foreach (var c in clusters)
            if (c.Count >= ReplayFormat.MinBatchSpawns) chosen = c;
        chosen ??= clusters[^1];
        return chosen.Min();
    }

    private int CountSpawns(int a, int b)
    {
        var n = 0;
        for (var i = a; i < b; i++)
            if (Segments[i].Opcode == SpawnOpcode) n++;
        return n;
    }

    /// <summary>
    /// Actual combat time within a pull: the real engage to the last combat action.
    ///
    /// <para>The engage is the first FirstAttack (first hit on the boss), which
    /// excludes the countdown, run-in and pre-pull casts.  If a pull's engage
    /// produced no fresh FirstAttack (a wipe-recovery re-pull) its first FirstAttack
    /// is actually a late add - detected by lots of combat already having happened
    /// before it - so we fall back to the first combat action there.</para>
    /// </summary>
    private uint CombatSpan(int startIndex, int endIndex)
    {
        var actMs = new List<uint>();
        var faMarks = new List<(uint Ms, int Before)>();
        for (var i = startIndex; i < endIndex; i++)
        {
            var op = Segments[i].Opcode;
            if (op == FirstAttackOpcode) faMarks.Add((Segments[i].Ms, actMs.Count));
            else if (CombatOps.Contains(op)) actMs.Add(Segments[i].Ms);
        }
        if (actMs.Count == 0) return 0;

        // engage = first FirstAttack with <15% of the pull's combat actions before it
        var startMs = actMs[0];
        foreach (var m in faMarks)
        {
            if (m.Before >= actMs.Count * 0.15) continue;
            startMs = m.Ms;
            break;
        }

        // end = drop trailing post-fight noise: peel off small gap-separated
        // clusters (DoT ticks / stray casts after the kill or wipe) until reaching
        // the dense combat. A mid-fight intermission gap is followed by a large
        // cluster, so the real fight is never trimmed.
        var end = actMs.Count - 1;
        while (end > 0)
        {
            var cs = end; // start of the cluster ending at `end`
            while (cs > 0 && actMs[cs] - actMs[cs - 1] <= ReplayFormat.CombatGapMs) cs--;
            if (cs > 0 && end - cs + 1 < ReplayFormat.CombatMinCluster) end = cs - 1;
            else break;
        }
        return actMs[end] > startMs ? actMs[end] - startMs : 0;
    }

    /// <summary>
    /// The countdown chapter that belongs to a pull.  Despite the name, the game
    /// logs a type-1 "Countdown" chapter at the <i>engage</i> - the moment the
    /// countdown ends and the boss fight starts (FFXIVClientStructs: Countdown =
    /// "Start of boss fight").  It therefore sits just <i>after</i> the pull's
    /// Start/Restart chapter, not before it.
    /// </summary>
    private Chapter? FindCountdownChapter(Chapter pullChapter, uint nextMs)
    {
        foreach (var c in Chapters)
        {
            if (c.Ms <= pullChapter.Ms) continue;
            if (c.Ms >= nextMs) break;
            if (ReplayFormat.CountdownChapterTypes.Contains(c.Type)) return c;
        }
        return null;
    }

    /// <summary>True when the segment is an all-zero waymark preset (nothing placed).</summary>
    public bool IsEmptyPreset(Segment s)
    {
        var b = SegPayload(s);
        if (b + 96 > Raw.Length) return true;
        for (var i = 0; i < 96; i++)
            if (Raw[b + i] != 0) return false;
        return true;
    }

    /// <summary>True when this file has any waymark worth carrying into a pull.</summary>
    public bool HasWaymarks() => Segments.Any(s =>
        (s.Opcode == WaymarkPresetOpcode && !IsEmptyPreset(s)) || s.Opcode == WaymarkOpcode);

    /// <summary>
    /// Everyone in the recording, in the order it introduces them.
    ///
    /// <para>The roster is one row per <i>character</i> the PlayerSpawn packets
    /// describe, keyed on the character key, not on the name text.  Two people can
    /// carry the same name - cross-world parties allow it outright, and an
    /// anonymize pass that numbered people by name used to produce it - and keying
    /// on the text drops everyone after the first.</para>
    ///
    /// <para>The byte scan still runs on top, because it is what finds a name's
    /// <i>other</i> occurrences, which is what a rename has to rewrite, and it
    /// picks up names no spawn packet describes.</para>
    /// </summary>
    private List<PlayerName> FindPlayers(int replayLength)
    {
        var limit = Math.Min(Raw.Length, ReplayFormat.DataStart + replayLength);
        var spawned = SpawnedCharacters(limit);
        var spawnNames = new HashSet<string>(spawned.Select(c => c.Name), StringComparer.Ordinal);
        var spawnFields = new HashSet<int>(spawned.SelectMany(c => c.Fields));

        // a 32-byte field: "First Last\0" + null padding, two cap-initial parts
        var found = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i + 32 <= limit; i++)
        {
            if (!IsUpper(Raw[i])) continue;
            var len = 0;
            while (len < 32 && IsNameChar(Raw[i + len])) len++;
            if (len is 0 or > 31) continue;
            var padded = true;
            for (var j = len; j < 32; j++)
                if (Raw[i + j] != 0) { padded = false; break; }
            if (!padded) continue;
            var s = Encoding.UTF8.GetString(Raw, i, len);
            // A name a spawn packet vouches for is a name whatever it looks like;
            // the shape test only has to answer for the rest.
            if (!spawnNames.Contains(s) && !LooksLikeName(s)) continue;
            if (!found.TryGetValue(s, out var offsets)) found[s] = offsets = new List<int>();
            offsets.Add(i);
        }

        var rows = new List<(int At, PlayerName Player)>();
        foreach (var c in spawned)
        {
            var offsets = new List<int>(c.Fields);
            // An occurrence outside a spawn name field cannot be pinned to one
            // character, so everyone carrying that name gets it and a rename still
            // clears the name everywhere the file writes it.  Where the name is
            // unique - which is every recording the game itself produced - that is
            // simply all of its occurrences.
            if (found.TryGetValue(c.Name, out var all))
                offsets.AddRange(all.Where(at => !spawnFields.Contains(at)));
            offsets.Sort();
            rows.Add((c.Fields[0],
                new PlayerName { Name = c.Name, CharacterKey = c.Key, Offsets = offsets }));
        }

        // Names no spawn packet describes still belong on the list.
        foreach (var (name, offsets) in found.Where(kv => !spawnNames.Contains(kv.Key)))
            rows.Add((offsets[0], new PlayerName { Name = name, Offsets = offsets }));

        return rows.OrderBy(r => r.At).Select(r => r.Player).ToList();

        static bool IsUpper(byte b) => b is >= 65 and <= 90;
        // digits are allowed so the scanner reads our own anonymized "Player N"
        // fields to the end; LooksLikeName still gates what counts as a name.
        static bool IsNameChar(byte b) =>
            b is >= 65 and <= 90 or >= 97 and <= 122 or >= 48 and <= 57 or 32 or 39 or 45;
    }

    /// <summary>
    /// Every character with a PlayerSpawn packet, first-seen order, with the offsets
    /// of that character's own name fields.
    /// </summary>
    private List<(ulong Key, string Name, List<int> Fields)> SpawnedCharacters(int limit)
    {
        var order = new List<(ulong Key, string Name, List<int> Fields)>();
        var spawnOp = PatchChain.Lookup(FilePatch, "PlayerSpawn");
        if (spawnOp is null) return order;

        var byKey = new Dictionary<ulong, int>();
        foreach (var seg in Segments)
        {
            if (seg.Opcode != spawnOp) continue;
            // The packet states its own size, and that is what says where the
            // fields are; an unrecognised size is one we have no offsets for.
            var lay = CharacterLayout.SpawnLayoutFor(seg.DataLength);
            if (lay is null) continue;
            var p = SegPayload(seg);
            var at = p + lay.Name;
            if (at + CharacterLayout.NameBytes > limit) continue;
            var end = at;
            while (end < at + CharacterLayout.NameBytes && Raw[end] != 0) end++;
            if (end == at) continue; // no name, nobody to list
            var key = BinaryPrimitives.ReadUInt64LittleEndian(Raw.AsSpan(p + lay.CharacterKey));
            var name = Encoding.UTF8.GetString(Raw, at, end - at);

            // A key of 0 says nothing about who this is, so those fall back to the
            // name - one row per distinct one, as before.
            var slot = key != 0
                ? byKey.TryGetValue(key, out var k) ? k : -1
                : order.FindIndex(o => o.Key == 0 && o.Name == name);
            if (slot >= 0) { order[slot].Fields.Add(at); continue; }
            if (key != 0) byKey[key] = order.Count;
            order.Add((key, name, new List<int> { at }));
        }
        return order;
    }

    internal static bool LooksLikeName(string s)
    {
        if (s.StartsWith("Player ", StringComparison.Ordinal))
        {
            var digits = s.AsSpan(7);
            if (digits.Length is >= 1 and <= 3 && !digits.ContainsAnyExcept("0123456789"))
                return true; // anonymized names this tool writes
        }
        var parts = s.Split(' ');
        if (parts.Length != 2) return false;
        foreach (var p in parts)
        {
            if (p.Length is < 2 or > 15) return false;
            if (p[0] is < 'A' or > 'Z') return false;
            foreach (var c in p)
                if (!char.IsAsciiLetter(c) && c != '\'' && c != '-') return false;
        }
        return true;
    }

    /// <summary>The header readout, as (label, value, tone) rows for display.</summary>
    public IReadOnlyList<(string Key, string Value, ReadoutTone Tone)> HeaderReadout()
    {
        var ts = U32(ReplayFormat.OffTimestamp);
        var info = Raw[ReplayFormat.OffInfo];
        var flags = new List<string>();
        if ((info & 1) != 0) flags.Add("up-to-date");
        if ((info & 2) != 0) flags.Add("locked");
        if ((info & 4) != 0) flags.Add("completed");

        var jobs = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var j = Raw[ReplayFormat.OffJobs + i];
            jobs.Add(ReplayFormat.JobAbbr.TryGetValue(j, out var a) ? a : j.ToString());
        }

        var build = I32(ReplayFormat.OffBuild);
        var os = U16(ReplayFormat.OffOsType) switch { 3 => "Windows", 5 => "Mac", var v => v.ToString() };
        var recorded = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

        return new (string, string, ReadoutTone)[]
        {
            ("format version", U16(ReplayFormat.OffVersion).ToString(), ReadoutTone.Plain),
            ("os", os, ReadoutTone.Plain),
            ("game build", build == OpcodeData.LatestGameBuild ? build.ToString() : $"{build} (outdated)",
                build == OpcodeData.LatestGameBuild ? ReadoutTone.Plain : ReadoutTone.Amber),
            ("recorded", recorded, ReadoutTone.Cyan),
            ("content id", U16(ReplayFormat.OffContentId).ToString(), ReadoutTone.Plain),
            ("total length", Display.Clock(TotalMs), ReadoutTone.Cyan),
            ("info flags", flags.Count > 0 ? string.Join(", ", flags) : "none", ReadoutTone.Plain),
            ("recorder", $"player {RecorderIndex + 1}", ReadoutTone.Amber),
            ("jobs", string.Join(" ", jobs), ReadoutTone.Plain),
            ("local CID", "0x" + U64(ReplayFormat.OffLocalCid).ToString("x"), ReadoutTone.Plain),
            ("replay length", Display.Bytes(ReplayLength), ReadoutTone.Plain),
            ("segments", Segments.Count.ToString("N0"), ReadoutTone.Plain),
        };
    }
}

public enum ReadoutTone
{
    Plain,
    Cyan,
    Amber,
}
