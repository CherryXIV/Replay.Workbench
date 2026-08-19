using System.Buffers.Binary;
using System.Text;

namespace ReplayWorkbench.Core;

/// <summary>One armor slot. Model/variant/dyes drive the in-arena model; the item
/// id is what the party-portrait list renders, which is stored separately.</summary>
public sealed class GearPiece
{
    public ushort Model { get; set; }
    public byte Variant { get; set; }
    public byte Dye1 { get; set; }
    public byte Dye2 { get; set; }
    /// <summary>Item id used by the party portrait; 0 when the file has no portrait for this character.</summary>
    public uint PortraitItemId { get; set; }

    public GearPiece Clone() => (GearPiece)MemberwiseClone();

    public bool SameAs(GearPiece o) =>
        Model == o.Model && Variant == o.Variant && Dye1 == o.Dye1 && Dye2 == o.Dye2 &&
        PortraitItemId == o.PortraitItemId;
}

/// <summary>A weapon, as the spawn packet packs it: [model][base][variant][dye].</summary>
public sealed class WeaponPiece
{
    public ushort Model { get; set; }
    public ushort Base { get; set; }
    public ushort Variant { get; set; }
    public ushort Dye { get; set; }

    public WeaponPiece Clone() => (WeaponPiece)MemberwiseClone();

    public bool SameAs(WeaponPiece o) =>
        Model == o.Model && Base == o.Base && Variant == o.Variant && Dye == o.Dye;

    public bool IsEmpty => Model == 0 && Base == 0 && Variant == 0 && Dye == 0;
}

/// <summary>Everything about how one character looks.</summary>
public sealed class CharacterAppearance
{
    public required Customize Customize { get; set; }
    public required GearPiece[] Gear { get; set; }
    public required WeaponPiece MainHand { get; set; }
    public required WeaponPiece OffHand { get; set; }
    public ushort Facewear { get; set; }

    /// <summary>Title sheet row id; 0 = no title.  Lives in the spawn packet only -
    /// the party portrait has no title field.</summary>
    public ushort Title { get; set; }

    /// <summary>The world the character is logged in on.  Spawn packet only.</summary>
    public ushort CurrentWorld { get; set; }

    /// <summary>The world the character belongs to.  Written to the spawn packet and
    /// to the PartyList roster, which carries its own copy.</summary>
    public ushort HomeWorld { get; set; }

    public bool HideHeadgear { get; set; }
    public bool HideWeapon { get; set; }

    public CharacterAppearance Clone() => new()
    {
        Customize = Customize.Clone(),
        Gear = Gear.Select(g => g.Clone()).ToArray(),
        MainHand = MainHand.Clone(),
        OffHand = OffHand.Clone(),
        Facewear = Facewear,
        Title = Title,
        CurrentWorld = CurrentWorld,
        HomeWorld = HomeWorld,
        HideHeadgear = HideHeadgear,
        HideWeapon = HideWeapon,
    };

    public bool SameAs(CharacterAppearance o) =>
        Customize.SameAs(o.Customize) &&
        Gear.Length == o.Gear.Length &&
        !Gear.Where((g, i) => !g.SameAs(o.Gear[i])).Any() &&
        MainHand.SameAs(o.MainHand) && OffHand.SameAs(o.OffHand) &&
        Facewear == o.Facewear && Title == o.Title &&
        CurrentWorld == o.CurrentWorld && HomeWorld == o.HomeWorld &&
        HideHeadgear == o.HideHeadgear && HideWeapon == o.HideWeapon;

    /// <summary>Dress in a job's artifact gear, the same set the anonymizer uses.</summary>
    public void ApplyJobGear(JobGear g)
    {
        for (var s = 0; s < Gear.Length && s < g.GearModels.Length; s++)
        {
            Gear[s].Model = (ushort)g.GearModels[s][0];
            Gear[s].Variant = (byte)g.GearModels[s][1];
            Gear[s].Dye1 = 0;
            Gear[s].Dye2 = 0;
            if (s < g.Gear.Length) Gear[s].PortraitItemId = (uint)g.Gear[s];
        }
        SetWeapon(MainHand, g.WeaponModel);
        SetWeapon(OffHand, g.WeaponSub);
        // AF gear is pointless if the character is hiding their helm or weapon.
        HideHeadgear = false;
        HideWeapon = false;

        static void SetWeapon(WeaponPiece w, int[]? m)
        {
            w.Model = (ushort)(m is not null ? m[0] : 0);
            w.Base = (ushort)(m is not null ? m[1] : 0);
            w.Variant = (ushort)(m is not null ? m[2] : 0);
            w.Dye = 0;
        }
    }
}

/// <summary>One character and the look to give them on export.</summary>
public sealed class CharacterEdit
{
    public required CharacterRecord Record { get; init; }
    public required CharacterAppearance Desired { get; init; }
}

/// <summary>A character found in a recording, and how they originally looked.</summary>
public sealed class CharacterRecord
{
    /// <summary>The per-character key this record was joined on; see
    /// <see cref="CharacterLayout.SpawnCharacterKey"/> for what it is and is not.</summary>
    public required ulong CharacterKey { get; init; }
    public required string Name { get; init; }
    public required byte Job { get; init; }
    /// <summary>How many PlayerSpawn packets carry this character.</summary>
    public required int SpawnPackets { get; init; }
    /// <summary>How many party-portrait member blocks carry this character.</summary>
    public required int PortraitBlocks { get; init; }
    public required CharacterAppearance Original { get; init; }

    public string JobName => ReplayFormat.JobAbbr.TryGetValue(Job, out var a) ? a : Job.ToString();
}

/// <summary>
/// Reads each character's looks out of a recording and writes edited looks back.
///
/// <para>Identity lives in two packets and they store gear differently, so an
/// edit is split: the customize block, facewear and both dye channels are written
/// to <b>both</b> (byte-identical layouts, verified against real recordings),
/// gear models and weapons go to the PlayerSpawn only - as are the title and the
/// current world, which no other packet carries - the home world additionally goes
/// to the PartyList roster, and the portrait's item
/// ids are edited as their own field. There is no model-to-item mapping without
/// the game's data files, so the two cannot be kept in sync automatically.</para>
/// </summary>
public static class CharacterEditor
{
    /// <summary>Every character with a PlayerSpawn in the file, in first-seen order.</summary>
    public static IReadOnlyList<CharacterRecord> Read(ReplayFile file)
    {
        var spawnOp = PatchChain.Lookup(file.FilePatch, "PlayerSpawn");
        if (spawnOp is null) return Array.Empty<CharacterRecord>();

        var raw = file.Raw;
        var span = raw.AsSpan();
        var order = new List<ulong>();
        var found = new Dictionary<ulong, (string Name, byte Job, CharacterAppearance App, int Spawns)>();

        foreach (var seg in file.Segments)
        {
            if (seg.Opcode != spawnOp) continue;
            var lay = CharacterLayout.SpawnLayoutFor(seg.DataLength);
            if (lay is null) continue;
            var p = file.SegPayload(seg);
            if (p + lay.Length > raw.Length) continue;
            var cid = BinaryPrimitives.ReadUInt64LittleEndian(span[(p + lay.CharacterKey)..]);
            if (cid == 0) continue;

            if (found.TryGetValue(cid, out var prev))
            {
                found[cid] = prev with { Spawns = prev.Spawns + 1 };
                continue;
            }
            order.Add(cid);
            found[cid] = (ReadName(raw, p + lay.Name), raw[p + lay.Job],
                ReadSpawn(span, p, lay), 1);
        }

        // Portrait blocks: gear item ids, and how many blocks each character is in.
        var portraitCounts = new Dictionary<ulong, int>();
        foreach (var seg in file.Segments)
        {
            if (seg.DataLength != CharacterLayout.PortraitLength) continue;
            var p = file.SegPayload(seg);
            if (p + CharacterLayout.PortraitLength > raw.Length) continue;
            for (var i = 0; i < CharacterLayout.PortraitMembers; i++)
            {
                var e = p + i * CharacterLayout.PortraitStride;
                var cid = BinaryPrimitives.ReadUInt64LittleEndian(span[(e + CharacterLayout.PortraitCharacterKey)..]);
                if (cid == 0 || !found.TryGetValue(cid, out var rec)) continue;
                portraitCounts[cid] = portraitCounts.GetValueOrDefault(cid) + 1;
                if (portraitCounts[cid] > 1) continue; // item ids from the first block only
                for (var s = 0; s < CharacterLayout.GearSlots; s++)
                    rec.App.Gear[s].PortraitItemId =
                        BinaryPrimitives.ReadUInt32LittleEndian(span[(e + CharacterLayout.PortraitGear + s * 4)..]);
            }
        }

        return order.Select(cid =>
        {
            var (name, job, app, spawns) = found[cid];
            return new CharacterRecord
            {
                CharacterKey = cid,
                Name = name,
                Job = job,
                SpawnPackets = spawns,
                PortraitBlocks = portraitCounts.GetValueOrDefault(cid),
                Original = app,
            };
        }).ToList();
    }

    private static string ReadName(byte[] raw, int at)
    {
        var end = at;
        while (end < at + CharacterLayout.NameBytes && raw[end] != 0) end++;
        return Encoding.UTF8.GetString(raw, at, end - at);
    }

    private static CharacterAppearance ReadSpawn(ReadOnlySpan<byte> span, int p, SpawnLayout lay)
    {
        var gear = new GearPiece[CharacterLayout.GearSlots];
        for (var s = 0; s < gear.Length; s++)
        {
            var at = p + lay.Gear + s * 4;
            gear[s] = new GearPiece
            {
                Model = BinaryPrimitives.ReadUInt16LittleEndian(span[at..]),
                Variant = span[at + 2],
                Dye1 = span[at + 3],
                Dye2 = span[p + lay.Dye2 + s],
            };
        }
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[(p + lay.Display)..]);
        return new CharacterAppearance
        {
            Customize = new Customize(span.Slice(p + lay.Customize, Customize.Length)),
            Gear = gear,
            MainHand = ReadWeapon(span, p + lay.Weapon),
            OffHand = ReadWeapon(span, p + lay.WeaponSub),
            Facewear = BinaryPrimitives.ReadUInt16LittleEndian(span[(p + lay.Facewear)..]),
            Title = BinaryPrimitives.ReadUInt16LittleEndian(span[(p + lay.Title)..]),
            CurrentWorld = BinaryPrimitives.ReadUInt16LittleEndian(span[(p + lay.CurrentWorld)..]),
            HomeWorld = BinaryPrimitives.ReadUInt16LittleEndian(span[(p + lay.HomeWorld)..]),
            HideHeadgear = (flags & CharacterLayout.DisplayHideHeadgear) != 0,
            HideWeapon = (flags & CharacterLayout.DisplayHideWeapon) != 0,
        };
    }

    private static WeaponPiece ReadWeapon(ReadOnlySpan<byte> span, int at) => new()
    {
        Model = BinaryPrimitives.ReadUInt16LittleEndian(span[at..]),
        Base = BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 2)..]),
        Variant = BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 4)..]),
        Dye = BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 6)..]),
    };

    /// <summary>
    /// Write edited looks into a finished export, in place and size-preserving.
    /// Runs before transpose, while packets still carry the file's own opcodes.
    ///
    /// <para>Only fields the user actually changed are written. That matters
    /// because a recording can hold a portrait block that disagrees with its own
    /// spawn packet, and writing a whole appearance would quietly overwrite the
    /// half nobody asked to touch.</para>
    /// </summary>
    /// <returns>A status fragment for the log, empty when nothing was edited.</returns>
    /// <param name="keyRemap">
    /// Original character key to its replacement, when an earlier pass has already
    /// rewritten them. The anonymizer scrambles the key, so without this the edits
    /// would be looking for people who are no longer in the buffer.
    /// </param>
    public static string Apply(byte[] bytes, string? filePatch, IEnumerable<CharacterEdit> edits,
        IReadOnlyDictionary<ulong, ulong>? keyRemap = null)
    {
        var plans = edits
            .Select(e => (Key: Remap(e.Record.CharacterKey), Plan: new WritePlan(e.Record.Original, e.Desired)))
            .Where(x => x.Plan.Any)
            .ToDictionary(x => x.Key, x => x.Plan);

        ulong Remap(ulong key) =>
            keyRemap is not null && keyRemap.TryGetValue(key, out var moved) ? moved : key;
        if (plans.Count == 0) return "";

        var spawnOp = PatchChain.Lookup(filePatch, "PlayerSpawn");
        if (spawnOp is null) return " · character edits skipped: no PlayerSpawn opcode for this patch";

        var span = bytes.AsSpan();
        var replayLen = BinaryPrimitives.ReadInt32LittleEndian(span[ReplayFormat.OffReplayLen..]);
        int spawnsWritten = 0, portraitsWritten = 0, rostersWritten = 0;
        var touched = new HashSet<ulong>();

        var off = 0;
        while (off < replayLen)
        {
            var b = ReplayFormat.DataStart + off;
            int op = BinaryPrimitives.ReadUInt16LittleEndian(span[b..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(span[(b + 2)..]);
            var p = b + ReplayFormat.SegHeader;

            var lay = op == spawnOp ? CharacterLayout.SpawnLayoutFor(len) : null;
            if (lay is not null)
            {
                var cid = BinaryPrimitives.ReadUInt64LittleEndian(span[(p + lay.CharacterKey)..]);
                if (plans.TryGetValue(cid, out var plan) && plan.TouchesSpawn)
                {
                    WriteSpawn(span, p, plan, lay);
                    spawnsWritten++;
                    touched.Add(cid);
                }
            }
            else if (len == CharacterLayout.PortraitLength)
            {
                for (var i = 0; i < CharacterLayout.PortraitMembers; i++)
                {
                    var e = p + i * CharacterLayout.PortraitStride;
                    var cid = BinaryPrimitives.ReadUInt64LittleEndian(span[(e + CharacterLayout.PortraitCharacterKey)..]);
                    if (!plans.TryGetValue(cid, out var plan) || !plan.TouchesPortrait) continue;
                    WritePortrait(span, e, plan);
                    portraitsWritten++;
                    touched.Add(cid);
                }
            }
            else if (len == CharacterLayout.PartyListLength)
            {
                for (var i = 0; i < CharacterLayout.PartyListMembers; i++)
                {
                    var e = p + i * CharacterLayout.PartyListStride;
                    var cid = BinaryPrimitives.ReadUInt64LittleEndian(span[(e + CharacterLayout.PartyListCharacterKey)..]);
                    if (!plans.TryGetValue(cid, out var plan) || !plan.TouchesPartyList) continue;
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        span[(e + CharacterLayout.PartyListHomeWorld)..], plan.Want.HomeWorld);
                    rostersWritten++;
                    touched.Add(cid);
                }
            }
            off += ReplayFormat.SegHeader + len;
        }

        if (spawnsWritten == 0 && portraitsWritten == 0 && rostersWritten == 0)
            return $" · character edits ({plans.Count}) matched nothing in this export";
        return $" · edited {touched.Count} character{(touched.Count == 1 ? "" : "s")} " +
               $"({spawnsWritten} spawns, {portraitsWritten} portraits, {rostersWritten} roster entries)";
    }

    /// <summary>Which fields differ between the original and the desired look.</summary>
    private sealed class WritePlan
    {
        public readonly CharacterAppearance Want;
        /// <summary>Which customize bytes changed. Tracked per byte, not per block, so
        /// an edit never rewrites the rest - a recording can hold a portrait whose
        /// customize disagrees with its own spawn, and the untouched bytes are not
        /// ours to unify.</summary>
        public readonly bool[] CustomizeByte = new bool[Core.Customize.Length];
        public readonly bool Weapons;
        public readonly bool Facewear;
        public readonly bool Title;
        public readonly bool CurrentWorld;
        public readonly bool HomeWorld;
        public readonly bool DisplayFlags;
        public readonly bool[] GearModel = new bool[CharacterLayout.GearSlots];
        public readonly bool[] GearDye = new bool[CharacterLayout.GearSlots];
        public readonly bool[] PortraitItem = new bool[CharacterLayout.GearSlots];

        public WritePlan(CharacterAppearance was, CharacterAppearance want)
        {
            Want = want;
            for (var i = 0; i < Core.Customize.Length; i++)
                CustomizeByte[i] = was.Customize[i] != want.Customize[i];
            Weapons = !was.MainHand.SameAs(want.MainHand) || !was.OffHand.SameAs(want.OffHand);
            Facewear = was.Facewear != want.Facewear;
            Title = was.Title != want.Title;
            CurrentWorld = was.CurrentWorld != want.CurrentWorld;
            HomeWorld = was.HomeWorld != want.HomeWorld;
            DisplayFlags = was.HideHeadgear != want.HideHeadgear || was.HideWeapon != want.HideWeapon;
            for (var s = 0; s < CharacterLayout.GearSlots && s < want.Gear.Length && s < was.Gear.Length; s++)
            {
                GearModel[s] = was.Gear[s].Model != want.Gear[s].Model || was.Gear[s].Variant != want.Gear[s].Variant;
                GearDye[s] = was.Gear[s].Dye1 != want.Gear[s].Dye1 || was.Gear[s].Dye2 != want.Gear[s].Dye2;
                PortraitItem[s] = was.Gear[s].PortraitItemId != want.Gear[s].PortraitItemId;
            }
        }

        public bool AnyCustomize => CustomizeByte.Any(x => x);

        public bool TouchesSpawn =>
            AnyCustomize || Weapons || Facewear || Title || CurrentWorld || HomeWorld ||
            DisplayFlags || GearModel.Any(x => x) || GearDye.Any(x => x);

        public bool TouchesPortrait =>
            AnyCustomize || Facewear || GearDye.Any(x => x) || PortraitItem.Any(x => x);

        /// <summary>The roster carries the home world and nothing else this editor
        /// touches, so it is only rewritten when that actually moved.</summary>
        public bool TouchesPartyList => HomeWorld;

        public bool Any => TouchesSpawn || TouchesPortrait || TouchesPartyList;
    }

    private static void WriteSpawn(Span<byte> span, int p, WritePlan plan, SpawnLayout lay)
    {
        var a = plan.Want;
        for (var i = 0; i < Customize.Length; i++)
            if (plan.CustomizeByte[i]) span[p + lay.Customize + i] = a.Customize[i];

        for (var s = 0; s < CharacterLayout.GearSlots && s < a.Gear.Length; s++)
        {
            var at = p + lay.Gear + s * 4;
            if (plan.GearModel[s])
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span[at..], a.Gear[s].Model);
                span[at + 2] = a.Gear[s].Variant;
            }
            if (!plan.GearDye[s]) continue;
            span[at + 3] = a.Gear[s].Dye1;
            span[p + lay.Dye2 + s] = a.Gear[s].Dye2;
        }

        if (plan.Weapons)
        {
            WriteWeapon(span, p + lay.Weapon, a.MainHand);
            WriteWeapon(span, p + lay.WeaponSub, a.OffHand);
        }
        if (plan.Facewear)
            BinaryPrimitives.WriteUInt16LittleEndian(span[(p + lay.Facewear)..], a.Facewear);
        if (plan.Title)
            BinaryPrimitives.WriteUInt16LittleEndian(span[(p + lay.Title)..], a.Title);
        if (plan.CurrentWorld)
            BinaryPrimitives.WriteUInt16LittleEndian(span[(p + lay.CurrentWorld)..], a.CurrentWorld);
        if (plan.HomeWorld)
            BinaryPrimitives.WriteUInt16LittleEndian(span[(p + lay.HomeWorld)..], a.HomeWorld);
        if (!plan.DisplayFlags) return;

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[(p + lay.Display)..]);
        flags = (ushort)(flags & ~CharacterLayout.DisplayHideGear);
        if (a.HideHeadgear) flags |= CharacterLayout.DisplayHideHeadgear;
        if (a.HideWeapon) flags |= CharacterLayout.DisplayHideWeapon;
        BinaryPrimitives.WriteUInt16LittleEndian(span[(p + lay.Display)..], flags);
    }

    private static void WritePortrait(Span<byte> span, int e, WritePlan plan)
    {
        var a = plan.Want;
        for (var i = 0; i < Customize.Length; i++)
            if (plan.CustomizeByte[i]) span[e + CharacterLayout.PortraitCustomize + i] = a.Customize[i];

        for (var s = 0; s < CharacterLayout.GearSlots && s < a.Gear.Length; s++)
        {
            if (plan.PortraitItem[s])
                BinaryPrimitives.WriteUInt32LittleEndian(
                    span[(e + CharacterLayout.PortraitGear + s * 4)..], a.Gear[s].PortraitItemId);
            if (!plan.GearDye[s]) continue;
            span[e + CharacterLayout.PortraitDye1 + s] = a.Gear[s].Dye1;
            span[e + CharacterLayout.PortraitDye2 + s] = a.Gear[s].Dye2;
        }

        if (plan.Facewear)
            BinaryPrimitives.WriteUInt16LittleEndian(span[(e + CharacterLayout.PortraitFacewear)..], a.Facewear);
    }

    private static void WriteWeapon(Span<byte> span, int at, WeaponPiece w)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span[at..], w.Model);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 2)..], w.Base);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 4)..], w.Variant);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 6)..], w.Dye);
    }
}
