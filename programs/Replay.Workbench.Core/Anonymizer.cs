using System.Buffers.Binary;
using System.Text;

namespace ReplayWorkbench.Core;

/// <summary>
/// Swap every party member to a chosen race (keeping their gender), redress them
/// in their job's artifact gear, blank names, and scramble object IDs.
///
/// <para>Identity leaks from three packets, so all are rewritten:</para>
/// <list type="bullet">
/// <item>PlayerSpawn (664B now, 656B on early Dawntrail recordings - the layout is
/// picked from the packet's own length) - the in-arena model: race + AF gear
/// (model IDs) + facewear/glasses id (stripped to 0) + title id (stripped to 0)
/// + current/home world (both set to <see cref="Anonymizer.AnonymousWorld"/>)
/// + status icon (set to <see cref="OnlineStatusData.InDuty"/>)</item>
/// <item>ActorControl category 504 - the status icon again, re-sent after the
/// spawn; left alone it puts the original icon back seconds into the playback</item>
/// <item>ModelEquip (72B) - a gear change made during the recording; it carries
/// the same armor models and weapons as the spawn, so left alone it walks the
/// original outfit back on screen the moment it plays</item>
/// <item>PartyList (3672B = 8x456 + trailer; matched by length) - the party panel's
/// roster, which keeps its own copy of each member's home world</item>
/// <item>party-member appearance (1408B = 8x176, gear stored as item IDs;
/// matched by length) - the "Party Members" portraits: race + AF gear +
/// facewear/glasses id, plus mainhand/offhand weapon model</item>
/// <item>every name string, replaced length-preserving across the file</item>
/// </list>
///
/// <para>Runs before transpose so packets are still in the file's own opcodes.</para>
/// </summary>
/// <summary>What the anonymizer did, plus the character-key remap it applied so
/// later passes can still find the people they were pointed at.</summary>
public sealed class AnonymizeResult
{
    public required string Note { get; init; }
    /// <summary>Original per-character key to its scrambled replacement.</summary>
    public required IReadOnlyDictionary<ulong, ulong> KeyRemap { get; init; }

    public static readonly AnonymizeResult None =
        new() { Note = "", KeyRemap = new Dictionary<ulong, ulong>() };
}

public static class Anonymizer
{
    // Field offsets all live in CharacterLayout, which the per-character editor
    // shares; these are local names for them so the two can never drift apart.
    // The ones that move between spawn layouts are read off the SpawnLayout the
    // packet's own length selects, not from a constant - see SpawnLayout.
    private const int PsGearN = CharacterLayout.SpawnGearBytes;
    private const int PsNameN = CharacterLayout.NameBytes;
    private const int DisplayHideGear = CharacterLayout.DisplayHideGear;
    private const int PsDye2N = CharacterLayout.GearSlots;

    private const int ApLen = CharacterLayout.PortraitLength;
    private const int ApStride = CharacterLayout.PortraitStride;
    private const int ApJob = CharacterLayout.PortraitJob;
    private const int ApGear = CharacterLayout.PortraitGear;
    private const int ApFace = CharacterLayout.PortraitFacewear;
    private const int ApCust = CharacterLayout.PortraitCustomize;

    private const int PlLen = CharacterLayout.PartyListLength;
    private const int PlStride = CharacterLayout.PartyListStride;
    private const int PlKey = CharacterLayout.PartyListCharacterKey;
    private const int PlHome = CharacterLayout.PartyListHomeWorld;

    /// <summary>The world every anonymized character is moved to.  One shared value
    /// rather than a random one per player: a world id is not a name, so the point is
    /// to stop the roster narrowing who the party was, and eight players scattered
    /// across eight invented worlds would say more about them than eight on one.</summary>
    public const ushort AnonymousWorld = 91;

    /// <summary>Rewrite spawn/appearance packets, names and object IDs in place.</summary>
    /// <returns>A status fragment for the log.</returns>
    public static AnonymizeResult Apply(byte[] bytes, string? filePatch, int race)
    {
        var span = bytes.AsSpan();
        var replayLen = BinaryPrimitives.ReadInt32LittleEndian(span[ReplayFormat.OffReplayLen..]);
        var spawnOp = PatchChain.Lookup(filePatch, "PlayerSpawn");
        var equipOp = PatchChain.Lookup(filePatch, "ModelEquip");
        // The status icon is re-sent after the spawn; see the note in pass 3.
        var iconOps = CharacterLayout.ActorControlOpNames
            .Select(n => PatchChain.Lookup(filePatch, n))
            .Where(o => o is not null).Select(o => o!.Value).ToArray();

        // Pass 1: gather the cast + object IDs from PlayerSpawn. Each PlayerSpawn's
        // segment-header oid (b+8) is the spawning player's own actor/object ID.
        //
        // People are counted per character key, not per name: two of them can carry
        // the same name (cross-world parties allow it outright), and numbering by
        // name hands both the same label - which is how a recording ends up with two
        // "Player 4"s and a roster three people short.
        var roster = new List<(ulong Key, string Name)>();
        var oids = new HashSet<uint>();
        // Actor id to job, so a ModelEquip can be redressed in the artifact set of
        // the job its owner's spawn packet gave them - see the branch in pass 3.
        var jobByOid = new Dictionary<uint, byte>();
        var keys = new List<ulong>();   // per-character keys, first-seen order
        var off = 0;
        while (off < replayLen)
        {
            int b = ReplayFormat.DataStart + off;
            int op = BinaryPrimitives.ReadUInt16LittleEndian(span[b..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            var p = b + ReplayFormat.SegHeader;
            var lay = op == spawnOp ? CharacterLayout.SpawnLayoutFor(len) : null;
            if (lay is not null)
            {
                var nm = ReadName(bytes, p + lay.Name);
                var oid = BinaryPrimitives.ReadUInt32LittleEndian(span[(b + 8)..]);
                if (oid != 0) { oids.Add(oid); jobByOid[oid] = bytes[p + lay.Job]; }
                var key = BinaryPrimitives.ReadUInt64LittleEndian(span[(p + lay.CharacterKey)..]);
                if (key != 0)
                {
                    if (!keys.Contains(key)) { keys.Add(key); roster.Add((key, nm)); }
                }
                // A key of 0 identifies nobody, so those fall back to the name.
                else if (nm.Length > 0 && !roster.Any(r => r.Name == nm)) roster.Add((0, nm));
            }
            off += ReplayFormat.SegHeader + len;
        }

        // The label each character gets, and the one each original name answers to
        // when it turns up somewhere that does not say whose it is.
        var labels = new Dictionary<ulong, string>();
        var nameLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < roster.Count; i++)
        {
            var (key, nm) = roster[i];
            var label = $"Player {i + 1}";
            if (key != 0) labels[key] = label;
            if (nm.Length > 0 && !nameLabels.ContainsKey(nm)) nameLabels[nm] = label;
        }

        // Build a random object ID for each player, kept in the player range (high
        // byte 0x10) so it still reads as a valid actor id. Avoid collisions with
        // any real id (and each other) so the length-preserving byte swaps below
        // can't chain or alias one player's packets onto another's.
        var idMap = new Dictionary<uint, uint>();
        var usedIds = new HashSet<uint>(oids);
        foreach (var id in oids)
        {
            uint r;
            do { r = 0x10000000u | (uint)Random.Shared.Next(0x01000000); } while (!usedIds.Add(r));
            idMap[id] = r;
        }

        // Pass 2: every occurrence of a real name.
        //
        // A name is stored in a fixed 32-byte field, so the label goes over the
        // whole field rather than only the bytes the real name happened to fill.
        // Splicing "Player 3" into the five bytes of "Ao Li" leaves "Playe": still
        // anonymous, but no longer a name - it reads as garbage in game and nothing
        // that scans the file back for name fields recognises it, so those players
        // drop off the tool's own roster. Occurrences outside such a field (a name
        // quoted mid-packet) have no room to grow, so they keep the truncating
        // splice; the field pass runs first so there are rarely any left.
        //
        // Matching is done against an untouched copy of the file. A label is itself
        // a name shape, so re-anonymizing an already-anonymized recording has the
        // sweep for "Player 4" walking over a *label* another player was just given
        // - which is how a recording ends up with two Player 4s and no Player 1.
        var nameHits = 0;
        if (nameLabels.Count > 0)
        {
            var probe = (byte[])bytes.Clone();
            foreach (var (nm, label) in nameLabels)
            {
                var need = Encoding.UTF8.GetBytes(nm);
                var lab = Encoding.UTF8.GetBytes(label);
                nameHits += ReplaceNameFields(bytes, need, lab, probe);

                var rep = new byte[need.Length];
                lab.AsSpan(0, Math.Min(lab.Length, rep.Length)).CopyTo(rep);
                nameHits += ReplaceBytes(bytes, need, rep, probe);
            }
        }

        // Pass 3: race (+ gear) on spawn and appearance packets, and the name field.
        // The per-character name is written here, after the sweep, so it is the last
        // word on who each spawn packet belongs to.
        int spawns = 0, dressed = 0, rosters = 0, icons = 0, equips = 0;
        off = 0;
        while (off < replayLen)
        {
            int b = ReplayFormat.DataStart + off;
            int op = BinaryPrimitives.ReadUInt16LittleEndian(span[b..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            var p = b + ReplayFormat.SegHeader;

            var lay = op == spawnOp ? CharacterLayout.SpawnLayoutFor(len) : null;
            if (lay is not null)
            {
                WriteCustomize(bytes, p + lay.Customize, race);
                // dress the in-arena model; a job with no artifact set is stripped instead
                var g = OpcodeData.GearForJob(bytes[p + lay.Job]);
                WriteGearModels(bytes, p + lay.Gear, g);
                WriteWeapon(span, p + lay.Weapon, g?.WeaponModel);   // mainhand -> AF weapon
                WriteWeapon(span, p + lay.WeaponSub, g?.WeaponSub);  // offhand  -> AF secondary (or cleared)
                BinaryPrimitives.WriteUInt16LittleEndian(span[(p + lay.Facewear)..], 0); // facewear leaks identity
                // A title is a Title-sheet row, the same value for everyone wearing
                // it, so it does not name anyone on its own - but a rare one narrows
                // the field hard, and it survives every other pass here untouched.
                BinaryPrimitives.WriteUInt16LittleEndian(span[(p + lay.Title)..], 0);
                // A status icon is not a name, but the mentor crowns and the like are
                // worn by few enough people to narrow a roster, and "Role-playing" on
                // one member is the sort of detail that identifies a group. Everyone is
                // set to In Duty - see OnlineStatusData.InDuty for why that rather than 0.
                bytes[p + lay.OnlineStatus] = OnlineStatusData.InDuty;
                // Home world is a short list a real person is on, and a visitor's
                // current world says which one they travelled to; both narrow the
                // party hard, so everyone is moved to the same world instead.
                BinaryPrimitives.WriteUInt16LittleEndian(span[(p + lay.CurrentWorld)..], AnonymousWorld);
                BinaryPrimitives.WriteUInt16LittleEndian(span[(p + lay.HomeWorld)..], AnonymousWorld);
                // unhide helm/weapon so the AF gear actually renders
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[(p + lay.Display)..]);
                BinaryPrimitives.WriteUInt16LittleEndian(span[(p + lay.Display)..],
                    (ushort)(flags & ~DisplayHideGear));
                Array.Clear(bytes, p + lay.Dye2, PsDye2N); // residual 2nd-dye bytes for redressed slots
                // The name goes in per character rather than by matching bytes, so
                // two people sharing a name still come out as two different players.
                var who = BinaryPrimitives.ReadUInt64LittleEndian(span[(p + lay.CharacterKey)..]);
                if (labels.TryGetValue(who, out var label))
                {
                    WriteNameField(bytes, p + lay.Name, label);
                    nameHits++;
                }
                spawns++;
            }
            else if (len == ApLen)
            {
                for (var i = 0; i < ApLen / ApStride; i++)
                {
                    var e = p + i * ApStride;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(span[e..]) == 0 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(span[(e + 4)..]) == 0) continue; // empty slot
                    int job = bytes[e + ApJob];
                    if (job is < 1 or > 42) continue; // not a member slot - leave it alone
                    WriteCustomize(bytes, e + ApCust, race);
                    var g = OpcodeData.GearForJob(job);
                    if (g is not null)
                    {
                        for (var s = 0; s < g.Gear.Length && s * 4 < 40; s++)
                            BinaryPrimitives.WriteUInt32LittleEndian(span[(e + ApGear + s * 4)..], (uint)g.Gear[s]);
                        dressed++;
                    }
                    else Array.Clear(bytes, e + ApGear, 40);
                    BinaryPrimitives.WriteUInt16LittleEndian(span[(e + ApFace)..], 0);
                }
            }
            else if (len == PlLen)
            {
                // The roster's own copy of the home world. Left alone it survives
                // every other pass here, and the party panel goes on naming worlds
                // the spawn packets no longer admit to.
                for (var i = 0; i < CharacterLayout.PartyListMembers; i++)
                {
                    var e = p + i * PlStride;
                    if (BinaryPrimitives.ReadUInt64LittleEndian(span[(e + PlKey)..]) == 0) continue; // empty slot
                    BinaryPrimitives.WriteUInt16LittleEndian(span[(e + PlHome)..], AnonymousWorld);
                    rosters++;
                }
            }
            else if (op == equipOp && len == CharacterLayout.ModelEquipLength)
            {
                // A gear change made mid-recording. It stores armor and weapons the
                // way the spawn packet does, and it is the last word on both from the
                // moment it plays, so redressing the spawn alone buys a few seconds of
                // anonymity and then hands the original glamour - and the original
                // weapons - straight back.
                //
                // Whose gear it is comes from the segment header's actor id, the same
                // join the status icons use, so the character is put back into the
                // artifact set of the job their own spawn gave them and the two packets
                // agree slot for slot. An actor with no spawn in this file falls back to
                // the packet's own job byte, and to bare models if that names no job -
                // conspicuous, but not identifying, which is the way round to fail here.
                var oid = BinaryPrimitives.ReadUInt32LittleEndian(span[(b + 8)..]);
                var job = jobByOid.TryGetValue(oid, out var known)
                    ? known
                    : bytes[p + CharacterLayout.ModelEquipJob];
                var g = OpcodeData.GearForJob(job);
                WriteGearModels(bytes, p + CharacterLayout.ModelEquipGear, g);
                WriteWeapon(span, p + CharacterLayout.ModelEquipWeapon, g?.WeaponModel);
                WriteWeapon(span, p + CharacterLayout.ModelEquipWeaponSub, g?.WeaponSub);
                Array.Clear(bytes, p + CharacterLayout.ModelEquipDye2, PsDye2N);
                equips++;
            }
            else if (Array.IndexOf(iconOps, op) >= 0 && len >= CharacterLayout.ActorControlMinLength &&
                     BinaryPrimitives.ReadUInt16LittleEndian(span[(p + CharacterLayout.ActorControlCategory)..]) ==
                     CharacterLayout.ActorControlSetStatusIcon)
            {
                // The spawn byte above is not the last word on the icon: these carry it
                // again, later, and the client acts on them from then on. They are
                // rewritten without asking whose they are - unlike the fields above,
                // every character in the recording is being anonymized to the same
                // status, so the actor id the segment header names does not matter.
                BinaryPrimitives.WriteUInt32LittleEndian(
                    span[(p + CharacterLayout.ActorControlParam1)..], OnlineStatusData.InDuty);
                icons++;
            }
            off += ReplayFormat.SegHeader + len;
        }

        // Pass 4: scramble object IDs - replace every little-endian occurrence of
        // each real player oid (segment headers + payload actor references) with
        // its random remap, length-preserving like the name swap.
        var idHits = 0;
        foreach (var (id, r) in idMap)
        {
            var need = new byte[4];
            var rep = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(need, id);
            BinaryPrimitives.WriteUInt32LittleEndian(rep, r);
            idHits += ReplaceBytes(bytes, need, rep);
        }

        // Pass 5: scramble the per-character key. It is only meaningful inside this
        // recording, but anyone holding the unedited original could otherwise line
        // the two files up on it and undo the renaming, so it goes too.
        var keyMap = ScrambleKeys(keys);
        var keyHits = 0;
        foreach (var (k, r) in keyMap)
        {
            var need = new byte[8];
            var rep = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(need, k);
            BinaryPrimitives.WriteUInt64LittleEndian(rep, r);
            keyHits += ReplaceBytes(bytes, need, rep);
        }

        var note = $" · anonymized {roster.Count} players ({spawns} spawns, {dressed} dressed, " +
                   $"{rosters} roster entries, {icons} status icons, " +
                   $"{equips} gear changes, " +
                   $"{roster.Count} names→{nameHits} refs, {idMap.Count} ids→{idHits} refs";
        note += keyMap.Count > 0 ? $", {keyMap.Count} keys→{keyHits} refs)" : ")";
        return new AnonymizeResult { Note = note, KeyRemap = keyMap };
    }

    /// <summary>
    /// Give every character a brand new random per-character key.
    ///
    /// <para>The whole 64 bits are randomised. That is safe because the value has no
    /// shape to respect: measured across the sample recordings, two captures share
    /// nothing at all in this field (0x788BEC00755A08B9, 0x5D20D38077F4F008,
    /// 0x209460800E850C7B ...), and neither of its halves ever appears in the header
    /// or chapter area, so nothing in the file cross-references it. Every occurrence
    /// is rewritten together, so the file stays internally consistent whatever value
    /// is chosen.</para>
    ///
    /// <para>An earlier version randomised only the bits that differed between the
    /// players of the recording, on the theory that the shared bits might be
    /// structural. They are not - and that rule degraded badly, giving a two-player
    /// recording one or two bits to work with and sometimes no usable value at all.</para>
    /// </summary>
    private static Dictionary<ulong, ulong> ScrambleKeys(List<ulong> keys)
    {
        var map = new Dictionary<ulong, ulong>();
        // Avoid colliding with a real key or with each other, so the swaps below
        // cannot chain or alias one character's packets onto another's.
        var used = new HashSet<ulong>(keys);
        foreach (var k in keys)
        {
            ulong candidate;
            do { candidate = NextUInt64(); } while (candidate == 0 || !used.Add(candidate));
            map[k] = candidate;
        }
        return map;
    }

    private static ulong NextUInt64()
    {
        Span<byte> b = stackalloc byte[8];
        Random.Shared.NextBytes(b);
        return BinaryPrimitives.ReadUInt64LittleEndian(b);
    }

    /// <summary>The name in a 32-byte field, up to its null terminator.</summary>
    private static string ReadName(byte[] bytes, int at)
    {
        var end = at;
        while (end < at + PsNameN && bytes[end] != 0) end++;
        return Encoding.UTF8.GetString(bytes, at, end - at);
    }

    /// <summary>Replace a whole 32-byte name field with <paramref name="label"/>.</summary>
    private static void WriteNameField(byte[] bytes, int at, string label)
    {
        var lab = Encoding.UTF8.GetBytes(label);
        Array.Clear(bytes, at, PsNameN);
        lab.AsSpan(0, Math.Min(lab.Length, PsNameN - 1)).CopyTo(bytes.AsSpan(at));
    }

    /// <summary>
    /// Dress a <see cref="CharacterLayout.GearSlots"/>-slot armor array -
    /// [model u16][variant u8][dye u8] per slot - in <paramref name="g"/>'s artifact
    /// models, or clear it outright when the job has no set.
    ///
    /// <para>Shared by PlayerSpawn and ModelEquip, which store armor identically.
    /// The two have to agree: ModelEquip is the last word on how a character looks
    /// from the moment it plays, so any slot they disagree about is a slot that
    /// changes on screen partway through the playback.</para>
    /// </summary>
    private static void WriteGearModels(byte[] bytes, int at, JobGear? g)
    {
        if (g is null) { Array.Clear(bytes, at, PsGearN); return; }
        var span = bytes.AsSpan();
        for (var s = 0; s < g.GearModels.Length && s * 4 < PsGearN; s++)
        {
            var o = at + s * 4;
            BinaryPrimitives.WriteUInt16LittleEndian(span[o..], (ushort)g.GearModels[s][0]);
            bytes[o + 2] = (byte)g.GearModels[s][1];
            bytes[o + 3] = 0;
        }
    }

    private static void WriteCustomize(byte[] bytes, int at, int race)
    {
        var gender = bytes[at + 1] & 1;  // preserve the player's gender
        Customize.Generic((byte)race, (byte)gender).Span.CopyTo(bytes.AsSpan(at, Customize.Length));
    }

    /// <summary>
    /// Write a weapon u64 [model][base][variant][dye=0].  <paramref name="wm"/> is
    /// [model, base, variant], or null to clear the slot (no weapon).
    /// </summary>
    private static void WriteWeapon(Span<byte> span, int at, int[]? wm)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span[at..], (ushort)(wm is not null ? wm[0] : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 2)..], (ushort)(wm is not null ? wm[1] : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 4)..], (ushort)(wm is not null ? wm[2] : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 6)..], 0); // dye
    }

    /// <summary>
    /// Overwrite every 32-byte name field holding <paramref name="name"/> - the name
    /// followed by null padding out to <see cref="CharacterLayout.NameBytes"/> - with
    /// <paramref name="label"/>, padded the same way.  The field is a fixed size, so
    /// the label may be longer than the name it replaces and the file still keeps
    /// its length.
    /// </summary>
    /// <param name="probe">Where to look for the name; defaults to the buffer being
    /// written.  Pass an untouched copy to keep one replacement from being matched
    /// and overwritten by the next.</param>
    private static int ReplaceNameFields(byte[] buf, byte[] name, byte[] label, byte[]? probe = null)
    {
        if (name.Length >= PsNameN || label.Length >= PsNameN) return 0;
        var src = probe ?? buf;
        var count = 0;
        for (var i = 0; i + PsNameN <= buf.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < PsNameN; j++)
                if (src[i + j] != (j < name.Length ? name[j] : (byte)0)) { hit = false; break; }
            if (!hit) continue;
            Array.Clear(buf, i, PsNameN);
            label.CopyTo(buf, i);
            count++;
            i += PsNameN - 1;
        }
        return count;
    }

    /// <summary>Overwrite every occurrence of <paramref name="needle"/> with <paramref name="repl"/> (same length).</summary>
    /// <param name="probe">Where to look; see <see cref="ReplaceNameFields"/>.</param>
    private static int ReplaceBytes(byte[] buf, byte[] needle, byte[] repl, byte[]? probe = null)
    {
        var n = needle.Length;
        var src = probe ?? buf;
        var count = 0;
        for (var i = 0; i <= buf.Length - n; i++)
        {
            var hit = true;
            for (var j = 0; j < n; j++)
                if (src[i + j] != needle[j]) { hit = false; break; }
            if (!hit) continue;
            repl.CopyTo(buf, i);
            count++;
            i += n - 1;
        }
        return count;
    }
}
