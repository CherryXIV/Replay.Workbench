namespace ReplayWorkbench.Core;

/// <summary>
/// Field offsets for the two packets that carry a player's looks.  Both were
/// read off real recordings (see the notes on each group); they are shared by the
/// blanket anonymizer and the per-character editor so the two can't drift.
/// </summary>
public static class CharacterLayout
{
    // ---- PlayerSpawn: the in-arena model -------------------------------------
    public const int SpawnLength = 664;

    /// <summary>
    /// An 8-byte per-character key at the very start of the payload, mirrored at
    /// the start of that character's party-portrait block - which is what makes it
    /// usable as the join between the two packets.
    ///
    /// <para>It sits where a content id would, but it does not behave like one and
    /// this tool does not claim it is: measured across the sample recordings it is
    /// unique per character <i>within</i> a recording (8 of 8 distinct in every
    /// file), yet the eight players of one recording share six of its eight bytes,
    /// and the same character carries a <b>different</b> value in a different
    /// recording. So it identifies a character within one recording, and nothing
    /// beyond it.</para>
    /// </summary>
    public const int SpawnCharacterKey = 0;

    /// <summary>Mainhand weapon, u64 packed as [model u16][base u16][variant u16][dye u16].
    /// Confirmed by diffing two captures that changed only the weapon glamour
    /// (item 44732 -> 2001/76/2, 22875 -> 2007/1/3).</summary>
    public const int SpawnWeapon = 0x30;
    public const int SpawnWeaponSub = 0x38;

    /// <summary>u16 display flags. 0x40 = hide headgear, 0x80 = hide weapon - set when
    /// a player toggles those off on the character screen.</summary>
    public const int SpawnDisplay = 0x74;
    public const int DisplayHideHeadgear = 0x40;
    public const int DisplayHideWeapon = 0x80;
    public const int DisplayHideGear = DisplayHideHeadgear | DisplayHideWeapon;

    public const int SpawnJob = 151;

    /// <summary>10 armor slots, 4 bytes each: [model u16][variant u8][dye u8].</summary>
    public const int SpawnGear = 540;
    public const int GearSlots = 10;
    public const int SpawnGearBytes = GearSlots * 4;

    /// <summary>Per-slot second dye channel (Dawntrail), one byte per slot, packed
    /// right after the gear array.</summary>
    public const int SpawnDye2 = SpawnGear + SpawnGearBytes; // 580

    /// <summary>Facewear/glasses model id (u16). 0 = none; confirmed against a
    /// known replay (Vivi = 457).</summary>
    public const int SpawnFacewear = 590;

    public const int SpawnName = 594;
    public const int NameBytes = 32;

    public const int SpawnCustomize = 626;

    // ---- Party portrait ("Party Members" list): 8 members of one stride --------
    // Offsets below were read off real recordings: the customize block is byte-
    // identical to the spawn's, the dye channels line up with the spawn's stains
    // (dye1 verified on every member of every sample, dye2 across 63 blocks).
    public const int PortraitLength = 1408;
    public const int PortraitStride = 176;
    public const int PortraitMembers = PortraitLength / PortraitStride; // 8

    /// <summary>The same per-character key as <see cref="SpawnCharacterKey"/>.</summary>
    public const int PortraitCharacterKey = 0;
    public const int PortraitJob = 17;
    /// <summary>10 slots of u32 <i>item</i> ids - not models, unlike the spawn packet.</summary>
    public const int PortraitGear = 80;
    public const int PortraitFacewear = 120;
    public const int PortraitCustomize = 124;
    public const int PortraitDye1 = 152;
    public const int PortraitDye2 = 164;

    /// <summary>Armor slot order, shared by both packets.</summary>
    public static readonly string[] GearSlotNames =
    {
        "Head", "Body", "Hands", "Legs", "Feet",
        "Earrings", "Necklace", "Bracelets", "Ring (R)", "Ring (L)",
    };
}
