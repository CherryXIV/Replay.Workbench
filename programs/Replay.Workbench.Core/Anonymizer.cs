using System.Buffers.Binary;
using System.Text;

namespace ReplayWorkbench.Core;

/// <summary>
/// Swap every party member to a chosen race (keeping their gender), redress them
/// in their job's artifact gear, blank names, and scramble object IDs.
///
/// <para>Identity leaks from three packets, so all are rewritten:</para>
/// <list type="bullet">
/// <item>PlayerSpawn (664B) - the in-arena model: race + AF gear (model IDs) +
/// facewear/glasses id (stripped to 0)</item>
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
    private const int PsLen = CharacterLayout.SpawnLength;
    private const int PsWeapon = CharacterLayout.SpawnWeapon;
    private const int PsWeaponSub = CharacterLayout.SpawnWeaponSub;
    private const int PsGear = CharacterLayout.SpawnGear;
    private const int PsGearN = CharacterLayout.SpawnGearBytes;
    private const int PsFace = CharacterLayout.SpawnFacewear;
    private const int PsName = CharacterLayout.SpawnName;
    private const int PsNameN = CharacterLayout.NameBytes;
    private const int PsCust = CharacterLayout.SpawnCustomize;
    private const int PsJob = CharacterLayout.SpawnJob;
    private const int PsDisplay = CharacterLayout.SpawnDisplay;
    private const int DisplayHideGear = CharacterLayout.DisplayHideGear;
    private const int PsDye2 = CharacterLayout.SpawnDye2;
    private const int PsDye2N = CharacterLayout.GearSlots;

    private const int ApLen = CharacterLayout.PortraitLength;
    private const int ApStride = CharacterLayout.PortraitStride;
    private const int ApJob = CharacterLayout.PortraitJob;
    private const int ApGear = CharacterLayout.PortraitGear;
    private const int ApFace = CharacterLayout.PortraitFacewear;
    private const int ApCust = CharacterLayout.PortraitCustomize;

    /// <summary>Rewrite spawn/appearance packets, names and object IDs in place.</summary>
    /// <returns>A status fragment for the log.</returns>
    public static AnonymizeResult Apply(byte[] bytes, string? filePatch, int race)
    {
        var span = bytes.AsSpan();
        var replayLen = BinaryPrimitives.ReadInt32LittleEndian(span[ReplayFormat.OffReplayLen..]);
        var spawnOp = PatchChain.Lookup(filePatch, "PlayerSpawn");

        // Pass 1: gather real names + object IDs from PlayerSpawn. Each PlayerSpawn's
        // segment-header oid (b+8) is the spawning player's own actor/object ID.
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var oids = new HashSet<uint>();
        var keys = new List<ulong>();   // per-character keys, first-seen order
        var off = 0;
        while (off < replayLen)
        {
            int b = ReplayFormat.DataStart + off;
            int op = BinaryPrimitives.ReadUInt16LittleEndian(span[b..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            var p = b + ReplayFormat.SegHeader;
            if (spawnOp is not null && op == spawnOp && len == PsLen)
            {
                var end = p + PsName;
                while (end < p + PsName + PsNameN && bytes[end] != 0) end++;
                var nm = Encoding.UTF8.GetString(bytes, p + PsName, end - (p + PsName));
                if (nm.Length > 0 && !labels.ContainsKey(nm)) labels[nm] = $"Player {labels.Count + 1}";
                var oid = BinaryPrimitives.ReadUInt32LittleEndian(span[(b + 8)..]);
                if (oid != 0) oids.Add(oid);
                var key = BinaryPrimitives.ReadUInt64LittleEndian(span[(p + CharacterLayout.SpawnCharacterKey)..]);
                if (key != 0 && !keys.Contains(key)) keys.Add(key);
            }
            off += ReplayFormat.SegHeader + len;
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

        // Pass 2: race (+ gear) on spawn and appearance packets.
        int spawns = 0, dressed = 0;
        off = 0;
        while (off < replayLen)
        {
            int b = ReplayFormat.DataStart + off;
            int op = BinaryPrimitives.ReadUInt16LittleEndian(span[b..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            var p = b + ReplayFormat.SegHeader;

            if (spawnOp is not null && op == spawnOp && len == PsLen)
            {
                WriteCustomize(bytes, p + PsCust, race);
                var g = OpcodeData.GearForJob(bytes[p + PsJob]);
                if (g is not null)
                {
                    // dress the in-arena model: [model:u16][variant:u8][stain:u8] per slot
                    for (var s = 0; s < g.GearModels.Length && s * 4 < PsGearN; s++)
                    {
                        var at = p + PsGear + s * 4;
                        BinaryPrimitives.WriteUInt16LittleEndian(span[at..], (ushort)g.GearModels[s][0]);
                        bytes[at + 2] = (byte)g.GearModels[s][1];
                        bytes[at + 3] = 0;
                    }
                    WriteWeapon(span, p + PsWeapon, g.WeaponModel);   // mainhand -> AF weapon
                    WriteWeapon(span, p + PsWeaponSub, g.WeaponSub);  // offhand  -> AF secondary (or cleared)
                }
                else
                {
                    Array.Clear(bytes, p + PsGear, PsGearN);
                    WriteWeapon(span, p + PsWeapon, null);
                    WriteWeapon(span, p + PsWeaponSub, null);
                }
                BinaryPrimitives.WriteUInt16LittleEndian(span[(p + PsFace)..], 0); // facewear leaks identity
                // unhide helm/weapon so the AF gear actually renders
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[(p + PsDisplay)..]);
                BinaryPrimitives.WriteUInt16LittleEndian(span[(p + PsDisplay)..],
                    (ushort)(flags & ~DisplayHideGear));
                Array.Clear(bytes, p + PsDye2, PsDye2N); // residual 2nd-dye bytes for redressed slots
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
            off += ReplayFormat.SegHeader + len;
        }

        // Pass 3: blank names everywhere (length-preserving).
        foreach (var (nm, label) in labels)
        {
            var need = Encoding.UTF8.GetBytes(nm);
            var rep = new byte[need.Length];
            var lab = Encoding.UTF8.GetBytes(label);
            lab.AsSpan(0, Math.Min(lab.Length, rep.Length)).CopyTo(rep);
            ReplaceBytes(bytes, need, rep);
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

        var note = $" · anonymized {labels.Count} players ({spawns} spawns, {dressed} dressed, " +
                   $"{idMap.Count} ids→{idHits} refs";
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

    /// <summary>Overwrite every occurrence of <paramref name="needle"/> with <paramref name="repl"/> (same length).</summary>
    private static int ReplaceBytes(byte[] buf, byte[] needle, byte[] repl)
    {
        var n = needle.Length;
        var count = 0;
        for (var i = 0; i <= buf.Length - n; i++)
        {
            var hit = true;
            for (var j = 0; j < n; j++)
                if (buf[i + j] != needle[j]) { hit = false; break; }
            if (!hit) continue;
            repl.CopyTo(buf, i);
            count++;
            i += n - 1;
        }
        return count;
    }
}
