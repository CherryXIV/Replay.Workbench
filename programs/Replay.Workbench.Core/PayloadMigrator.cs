using System.Buffers.Binary;

namespace ReplayWorkbench.Core;

/// <summary>
/// How one packet's payload moves from an older layout to the one the current
/// client expects.
/// </summary>
public sealed record PayloadMigration
{
    public required string Packet { get; init; }
    /// <summary>The old payload size this applies to.</summary>
    public required int From { get; init; }
    /// <summary>The size the current client expects.</summary>
    public required int To { get; init; }

    /// <summary>
    /// Runs of zero bytes to splice in, in <i>old</i> payload coordinates - each
    /// offset is the byte its run goes in front of, so the entries never shift one
    /// another.  Whatever they leave short of <see cref="To"/> is zero padding at
    /// the tail.
    /// </summary>
    public required IReadOnlyList<(int At, int Count)> Inserts { get; init; }

    public byte[] Migrate(ReadOnlySpan<byte> payload)
    {
        var outp = new byte[To];
        int read = 0, write = 0;
        foreach (var (at, count) in Inserts.OrderBy(i => i.At))
        {
            if (at > payload.Length || write + count > To) break;
            var run = Math.Min(at - read, To - write);
            if (run > 0) payload.Slice(read, run).CopyTo(outp.AsSpan(write));
            write += run + count; // the spliced bytes are already zero
            read = at;
        }
        var rest = Math.Min(payload.Length - read, To - write);
        if (rest > 0) payload.Slice(read, rest).CopyTo(outp.AsSpan(write));
        return outp;
    }
}

public sealed class MigrateResult
{
    public required byte[] Bytes { get; init; }
    public required string Note { get; init; }
    /// <summary>Packet types left at a size the client won't accept, and why.</summary>
    public required IReadOnlyList<string> Blocked { get; init; }

    public static MigrateResult Untouched(byte[] bytes) =>
        new() { Bytes = bytes, Note = "", Blocked = Array.Empty<string>() };
}

/// <summary>
/// Resize the packets whose struct grew, so a recording from an older patch is
/// one the current client will actually read.
///
/// <para>Transpose renumbers opcodes, which is the whole job only while a packet's
/// layout holds still.  It doesn't for everything: between 7.16h and 7.55h2 five
/// packet types changed size, and a client handed a 112-byte InitZone where it
/// expects 136 stops reading at packet zero.  So this runs alongside transpose -
/// neither is any use without the other, and a file with one applied and not the
/// other is worse than the original.</para>
///
/// <para><b>Where the layouts come from.</b>  A 7.16h and a 7.55h recording of the
/// same duty, which makes the comparison controlled: the same cast, the same
/// arena, and a field that is constant in one is constant in the other.  Each
/// packet was pinned on its own evidence, and they are not equally certain - see
/// the notes on <see cref="Migrations"/>.  All of it was confirmed the only way
/// that finally counts, by loading the converted recording in the live client.</para>
///
/// <para>Runs before transpose, so packets are still on the file's own opcodes and
/// are found by name in the file's own patch.  Doing it the other way round means
/// picking packets by numbers that meant something else in the older patch.</para>
/// </summary>
public static class PayloadMigrator
{
    /// <summary>What each packet's payload must measure on the current patch.
    /// Measured across 7.51-7.55h2 recordings.</summary>
    public static readonly IReadOnlyDictionary<string, int> TargetSize = new Dictionary<string, int>
    {
        ["PlayerSpawn"] = 664,
        ["NpcSpawn"] = 656,
        ["ActorControlSelf"] = 40,
        ["Countdown"] = 64,
        ["InitZone"] = 136,
    };

    /// <summary>
    /// The measured moves.
    ///
    /// <list type="bullet">
    /// <item><b>PlayerSpawn</b> - proven rather than inferred.  Every field was
    /// located by cross-referencing the party-portrait packet, whose customize
    /// block, job byte and both dye channels are byte-identical to the spawn's and
    /// which did not change size.  That puts job at +2 and everything from the gear
    /// array onward at +4, so the inserts fall in (126,140) and (157,164) - runs of
    /// zero padding in <i>both</i> patches, which is why splicing zeros reproduces
    /// the real layout exactly.  The last 4 land in the tail, zero in both.</item>
    /// <item><b>NpcSpawn</b> - same shape, established statistically.  A per-byte
    /// value-distribution profile steps cleanly 0 → +2 → +4, and each transition is
    /// confirmed by a distinctive field landing on its counterpart: the 0x00/0x10
    /// byte at old 146 → new 148 and 0x00/0x01 at old 147 → new 149 (+2), while
    /// 0x00/0x30 at old 148 → new 152 (+4).</item>
    /// <item><b>ActorControlSelf</b> - plain tail growth. The 8 added bytes are zero
    /// in all 4488 current-patch samples and matched packets are byte-identical
    /// across the first 32.</item>
    /// <item><b>Countdown</b> - a 16-byte head appeared. Object id old +0 → new +16,
    /// the second id +4 → +20 and the name +11 → +27 all agree.  The first 8 bytes
    /// of the new head are the initiating player's character key, filled in from the
    /// file rather than left zero (see <see cref="Apply"/>).</item>
    /// </list>
    ///
    /// <para>InitZone is deliberately absent: it <i>deletes</i> 8 bytes as well as
    /// adding 32, so no splice can express it.  It is rebuilt from a template
    /// instead.</para>
    /// </summary>
    public static readonly IReadOnlyList<PayloadMigration> Migrations = new[]
    {
        new PayloadMigration
        {
            Packet = "PlayerSpawn", From = 656, To = 664,
            Inserts = new[] { (126, 2), (157, 2) }, // remaining 4 pad the tail
        },
        new PayloadMigration
        {
            Packet = "NpcSpawn", From = 648, To = 656,
            Inserts = new[] { (124, 2), (148, 2) }, // remaining 4 pad the tail
        },
        new PayloadMigration
        {
            Packet = "ActorControlSelf", From = 32, To = 40,
            Inserts = Array.Empty<(int, int)>(),
        },
        new PayloadMigration
        {
            Packet = "Countdown", From = 48, To = 64,
            Inserts = new[] { (0, 16) },
        },
    };

    // InitZone's recording-specific fields. Everything else in the packet is
    // either constant across current samples or always zero, so it can come from a
    // template. +0x06 is confirmed: it equals the header's content id in every
    // recording checked, old ones included.
    private static readonly (int At, int Len)[] InitZoneIdent =
        { (0x00, 2), (0x02, 2), (0x04, 2), (0x06, 2), (0x10, 1), (0x13, 1) };

    private const int InitZoneOldPosition = 0x50;
    private const int InitZoneNewPosition = 0x68;
    private const int InitZonePositionLen = 12;

    /// <summary>Payload sizes that are old enough to need work, by packet name.</summary>
    private static PayloadMigration? For(string packet, int size) =>
        Migrations.FirstOrDefault(m => m.Packet == packet && m.From == size);

    /// <summary>
    /// Does this recording carry packets the current client would reject on size?
    /// </summary>
    public static bool IsNeeded(ReplayFile file) => OldSized(file).Count > 0;

    /// <summary>
    /// True when the file has an InitZone that has to be rebuilt, which is the one
    /// case that needs a template recording from the caller.
    /// </summary>
    public static bool NeedsInitZoneTemplate(ReplayFile file) =>
        OldSized(file).ContainsKey("InitZone");

    /// <summary>Packet name to the old size it was found at.</summary>
    public static IReadOnlyDictionary<string, int> OldSized(ReplayFile file)
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, target) in TargetSize)
        {
            var op = PatchChain.Lookup(file.FilePatch, name);
            if (op is null) continue;
            foreach (var seg in file.Segments)
            {
                if (seg.Opcode != op || seg.DataLength == target) continue;
                found[name] = seg.DataLength;
                break;
            }
        }
        return found;
    }

    /// <summary>
    /// Lift an InitZone payload out of a recording that is already on the current
    /// layout, to rebuild an old one against.
    ///
    /// <para>Prefer a recording of the <i>same duty</i>: InitZone carries the
    /// territory, the content id and the arena spawn position, so a same-duty
    /// template needs almost nothing transplanted into it.  The template does not
    /// have to be on the latest patch, only new enough to already be
    /// <see cref="TargetSize"/> bytes - its opcode is resolved in whatever patch it
    /// actually is.</para>
    /// </summary>
    /// <returns>The payload, or null with <paramref name="error"/> set.</returns>
    public static byte[]? ReadInitZoneTemplate(byte[] recording, string name, out string? error)
    {
        error = null;
        ReplayFile file;
        try { file = ReplayFile.Parse(recording, name); }
        catch (Exception e) { error = e.Message; return null; }

        var op = PatchChain.Lookup(file.FilePatch, "InitZone");
        if (op is null)
        {
            error = $"{name}: no InitZone opcode for {file.FilePatch ?? "an unknown patch"}";
            return null;
        }
        var want = TargetSize["InitZone"];
        foreach (var seg in file.Segments)
        {
            if (seg.Opcode != op) continue;
            if (seg.DataLength != want)
            {
                error = $"{name} reads as {file.FilePatch} and its InitZone is {seg.DataLength} bytes, " +
                        $"not {want} - the template has to be recent enough to already be on the current layout";
                return null;
            }
            return recording.AsSpan(file.SegPayload(seg), want).ToArray();
        }
        error = $"{name}: no InitZone packet in the file";
        return null;
    }

    /// <summary>
    /// Rebuild an InitZone against a working one: start from a payload the live
    /// client already accepted and overwrite only the fields that identify this
    /// recording.  Going this direction removes the guess - anything unidentified
    /// keeps a value known to work rather than one we invented.
    /// </summary>
    private static byte[] RebuildInitZone(ReadOnlySpan<byte> old, byte[] template)
    {
        var outp = (byte[])template.Clone();
        foreach (var (at, len) in InitZoneIdent)
            if (at + len <= old.Length) old.Slice(at, len).CopyTo(outp.AsSpan(at));
        var from = old.Length == TargetSize["InitZone"] ? InitZoneNewPosition : InitZoneOldPosition;
        if (from + InitZonePositionLen <= old.Length)
            old.Slice(from, InitZonePositionLen).CopyTo(outp.AsSpan(InitZoneNewPosition));
        return outp;
    }

    /// <summary>
    /// Object id to character key, off this file's PlayerSpawn packets.  Both spawn
    /// layouts keep the key at payload +0 and the spawning player's object id in
    /// the segment header, so this does not care which one it is reading.
    /// </summary>
    private static Dictionary<uint, ulong> SpawnKeys(byte[] bytes, int replayLen, int? spawnOp)
    {
        var map = new Dictionary<uint, ulong>();
        if (spawnOp is null) return map;
        var span = bytes.AsSpan();
        var off = 0;
        while (off < replayLen)
        {
            var b = ReplayFormat.DataStart + off;
            if (b + ReplayFormat.SegHeader > bytes.Length) break;
            int op = BinaryPrimitives.ReadUInt16LittleEndian(span[b..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            if (op == spawnOp && len >= 8 && b + ReplayFormat.SegHeader + 8 <= bytes.Length)
            {
                var oid = BinaryPrimitives.ReadUInt32LittleEndian(span[(b + 8)..]);
                var key = BinaryPrimitives.ReadUInt64LittleEndian(span[(b + ReplayFormat.SegHeader)..]);
                if (oid != 0 && key != 0) map[oid] = key;
            }
            off += ReplayFormat.SegHeader + len;
        }
        return map;
    }

    /// <summary>
    /// Resize every packet that needs it, and fix up what the sizes invalidate:
    /// the replay length, and every chapter offset, which points into the data
    /// stream and so moves by however many bytes grew before it.
    /// </summary>
    /// <param name="initZoneTemplate">An InitZone payload from
    /// <see cref="ReadInitZoneTemplate"/>.  Without one the InitZone is left as it
    /// is and reported in <see cref="MigrateResult.Blocked"/>, because a rebuilt
    /// guess is worse than a clearly-stated gap.</param>
    public static MigrateResult Apply(byte[] bytes, string? filePatch, byte[]? initZoneTemplate)
    {
        var opToName = new Dictionary<int, string>();
        foreach (var name in TargetSize.Keys)
        {
            var op = PatchChain.Lookup(filePatch, name);
            if (op is not null) opToName[op.Value] = name;
        }
        if (opToName.Count == 0) return MigrateResult.Untouched(bytes);

        var span = bytes.AsSpan();
        var replayLen = BinaryPrimitives.ReadInt32LittleEndian(span[ReplayFormat.OffReplayLen..]);
        var keys = SpawnKeys(bytes, replayLen, PatchChain.Lookup(filePatch, "PlayerSpawn"));

        var body = new List<byte>(bytes.Length);
        var shifts = new List<(int End, int Grown)>(); // data-stream offset -> growth so far
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var blocked = new SortedSet<string>(StringComparer.Ordinal);
        int off = 0, grown = 0;

        while (off < replayLen)
        {
            var b = ReplayFormat.DataStart + off;
            if (b + ReplayFormat.SegHeader > bytes.Length) break;
            int op = BinaryPrimitives.ReadUInt16LittleEndian(span[b..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            var p = b + ReplayFormat.SegHeader;
            if (p + len > bytes.Length) break;

            var header = bytes.AsSpan(b, ReplayFormat.SegHeader).ToArray();
            var payload = bytes.AsSpan(p, len);
            byte[]? resized = null;

            if (opToName.TryGetValue(op, out var name) && len != TargetSize[name])
            {
                if (name == "InitZone")
                {
                    if (initZoneTemplate is not null) resized = RebuildInitZone(payload, initZoneTemplate);
                    else blocked.Add($"InitZone is {len} bytes and needs a template recording to rebuild from");
                }
                else if (For(name, len) is { } m)
                {
                    resized = m.Migrate(payload);
                    // Countdown's new head opens with the character key; the packet
                    // still carries the player's object id, so it can be looked up.
                    if (name == "Countdown" && len >= 4 &&
                        keys.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(payload), out var key))
                        BinaryPrimitives.WriteUInt64LittleEndian(resized, key);
                }
                else
                {
                    blocked.Add($"{name} is {len} bytes, a size no measured layout covers");
                }
            }

            if (resized is not null)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)resized.Length);
                counts[name!] = counts.GetValueOrDefault(name!) + 1;
                grown += resized.Length - len;
                body.AddRange(header);
                body.AddRange(resized);
            }
            else
            {
                body.AddRange(header);
                body.AddRange(payload.ToArray());
            }

            off += ReplayFormat.SegHeader + len;
            shifts.Add((off, grown));
        }

        if (counts.Count == 0 && blocked.Count == 0) return MigrateResult.Untouched(bytes);

        var tailAt = ReplayFormat.DataStart + replayLen;
        var trailing = tailAt < bytes.Length ? bytes.Length - tailAt : 0;
        var outp = new byte[ReplayFormat.DataStart + body.Count + trailing];
        bytes.AsSpan(0, ReplayFormat.DataStart).CopyTo(outp);
        body.CopyTo(outp, ReplayFormat.DataStart);
        if (trailing > 0) bytes.AsSpan(tailAt).CopyTo(outp.AsSpan(ReplayFormat.DataStart + body.Count));

        var ov = outp.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(ov[ReplayFormat.OffReplayLen..], body.Count);

        var clen = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(ov[ReplayFormat.HeaderSize..]),
            0, ReplayFormat.MaxChapters);
        for (var i = 0; i < clen; i++)
        {
            var e = ReplayFormat.HeaderSize + 4 + i * ReplayFormat.ChapterEntry;
            var at = BinaryPrimitives.ReadUInt32LittleEndian(ov[(e + 4)..]);
            var delta = 0;
            foreach (var (end, g) in shifts)
            {
                if (end > at) break;
                delta = g;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(ov[(e + 4)..], (uint)(at + delta));
        }

        var detail = string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}"));
        var note = counts.Count > 0
            ? $" · resized {detail} ({grown:+#,0;-#,0;0} bytes)"
            : "";
        foreach (var why in blocked) note += $" · NOT resized: {why}";
        return new MigrateResult { Bytes = outp, Note = note, Blocked = blocked.ToList() };
    }
}
