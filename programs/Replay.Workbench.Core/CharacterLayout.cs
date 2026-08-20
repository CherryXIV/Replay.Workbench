namespace ReplayWorkbench.Core;

/// <summary>
/// Where one player's looks sit inside a PlayerSpawn payload.
///
/// <para>These are deliberately <b>not</b> constants.  The packet has grown over
/// Dawntrail, so a recording made before it grew keeps every field somewhere
/// else, and a tool that assumes one layout does not fail loudly on the other -
/// it matches no spawn packet at all, lists nobody, and reports success while
/// leaving every real name in the file.</para>
///
/// <para>Which layout a packet uses is answered by the packet: the segment header
/// carries its payload length, and each layout has its own.  So nothing here has
/// to know which patch first moved a field, and a recording from a patch nobody
/// has a sample of still reads correctly as long as its spawn packet is a size we
/// have seen.  Resolution is always gated on the PlayerSpawn opcode first,
/// because the sizes are only unique among PlayerSpawns - 7.55h's <i>NpcSpawn</i>
/// is 656 bytes, the same as 7.16h's PlayerSpawn.</para>
/// </summary>
public sealed record SpawnLayout
{
    /// <summary>Payload length of a PlayerSpawn packet in this layout - the key
    /// that selects it.</summary>
    public required int Length { get; init; }

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
    public required int CharacterKey { get; init; }

    /// <summary>
    /// The title worn under the character's name, u16, as a row id in the game's
    /// <c>Title</c> sheet.  0 = no title.
    ///
    /// <para>Derived from three recordings of one character that differ only in the
    /// title worn: 8 (Starcaller), 9 (The Exterminator) and 865 (the newest title
    /// that character owns).  The 8 -> 865 pair moves both bytes, which is what
    /// pins the field as a u16 rather than a byte followed by padding; and 865 is
    /// far too large to be an index into one character's unlocked-title list, so
    /// the value is the sheet row itself and means the same thing in every file.
    /// Every NpcSpawn in those recordings reads 0 here, as an NPC should.</para>
    ///
    /// <para>The read is anchored on both sides: the u16 at +2 is 0 in every
    /// sample, and the two after it are the current and home world ids - which is
    /// the field order this packet is known to have.</para>
    /// </summary>
    public required int Title { get; init; }

    /// <summary>
    /// The world the character is logged in on (u16), and the world they belong to
    /// (u16, two bytes later).  Equal for anyone sitting on their own world.
    ///
    /// <para>Measured on an eight-player recording: six distinct values across the
    /// party (40, 63, 65, 73, 81, 408), and two members who separate the pair -
    /// one reading 65/81 and one 65/408, both visitors on the recorder's own world
    /// 65.  That is what tells the two fields apart; a recording of a party sitting
    /// at home would have shown one repeated number and proved nothing.</para>
    /// </summary>
    public required int CurrentWorld { get; init; }

    /// <summary>See <see cref="CurrentWorld"/>. Mirrored in the PartyList packet -
    /// see <see cref="CharacterLayout.PartyListHomeWorld"/> - which carries the home
    /// world only, so both have to be written to move a character's world.</summary>
    public required int HomeWorld { get; init; }

    /// <summary>Mainhand weapon, u64 packed as [model u16][base u16][variant u16][dye u16].
    /// Confirmed by diffing two captures that changed only the weapon glamour
    /// (item 44732 -> 2001/76/2, 22875 -> 2007/1/3).</summary>
    public required int Weapon { get; init; }
    public required int WeaponSub { get; init; }

    /// <summary>u16 display flags. 0x40 = hide headgear, 0x80 = hide weapon - set when
    /// a player toggles those off on the character screen.</summary>
    public required int Display { get; init; }

    /// <summary>
    /// The status icon beside the character's name (u8), as a row id in the game's
    /// <c>OnlineStatus</c> sheet - see <see cref="OnlineStatusData"/>.
    ///
    /// <para>Pinned by ten recordings of one character that differ only in the status
    /// set in the search-info window: this is the sole byte of the spawn packet that
    /// moves between them, and each value is exactly its sheet row. NPCs read 0,
    /// which is the anchor on the other side - the field is only ever populated for
    /// a player.</para>
    ///
    /// <para>It is not the only copy. <see cref="CharacterLayout.ActorControlCategory"/>
    /// carries the same value in packets that land after the spawn, and those are
    /// what the client acts on from then on, so a status edit has to write both -
    /// see <see cref="CharacterEditor"/>.</para>
    /// </summary>
    public required int OnlineStatus { get; init; }

    public required int Job { get; init; }

    /// <summary>10 armor slots, 4 bytes each: [model u16][variant u8][dye u8].</summary>
    public required int Gear { get; init; }

    /// <summary>Per-slot second dye channel (Dawntrail), one byte per slot, packed
    /// right after the gear array.</summary>
    public required int Dye2 { get; init; }

    /// <summary>Facewear/glasses model id (u16). 0 = none; confirmed against a
    /// known replay (Vivi = 457).</summary>
    public required int Facewear { get; init; }

    public required int Name { get; init; }

    public required int Customize { get; init; }
}

/// <summary>
/// Field offsets for the two packets that carry a player's looks.  Both were
/// read off real recordings (see the notes on each group); they are shared by the
/// blanket anonymizer and the per-character editor so the two can't drift.
/// </summary>
public static class CharacterLayout
{
    // ---- PlayerSpawn: the in-arena model -------------------------------------
    //
    // Two layouts are known, and they were derived rather than read off a struct
    // definition: each field was pinned by cross-referencing the party-portrait
    // packet - whose customize block, job byte and both dye channels are byte-
    // identical to the spawn's - against the eight players of a recording. Running
    // that derivation against a current recording reproduces the 664-byte offsets
    // below exactly, which is what makes the 656-byte result trustworthy.
    //
    // Between the two, the packet gained 8 bytes: two separate 2-byte inserts
    // (before Job, and between Job and Gear) plus 4 at the tail.  Both inserts land
    // inside runs of zero padding, so no field this tool addresses sits between
    // them; everything from Gear onward simply shifts by 4, and the head - key,
    // both weapons, the display flags - does not move at all.

    /// <summary>Current Dawntrail layout. Measured on 7.51h2, 7.55h and 7.55h2
    /// recordings.</summary>
    public static readonly SpawnLayout Spawn664 = new()
    {
        Length = 664,
        CharacterKey = 0,
        Title = 16,
        CurrentWorld = 20,
        HomeWorld = 22,
        OnlineStatus = 0x1B,
        Weapon = 0x30,
        WeaponSub = 0x38,
        Display = 0x74,
        Job = 151,
        Gear = 540,
        Dye2 = 580,   // Gear + GearSlots * 4
        Facewear = 590,
        Name = 594,
        Customize = 626,
    };

    /// <summary>Early Dawntrail layout. Measured on a 7.16h recording; which patch
    /// first grew the packet is not known, and does not need to be - the length
    /// picks the layout.</summary>
    public static readonly SpawnLayout Spawn656 = new()
    {
        Length = 656,
        CharacterKey = 0,
        // Inferred, not measured: every sample carrying a title, a cross-world
        // player or a set online status is 664-byte. All four sit in the head, which
        // the note above records as not having moved between the two layouts, so they
        // keep their offsets here unless a 656-byte sample says otherwise.
        Title = 16,
        CurrentWorld = 20,
        HomeWorld = 22,
        OnlineStatus = 0x1B,
        Weapon = 0x30,
        WeaponSub = 0x38,
        Display = 0x74,
        Job = 149,
        Gear = 536,
        Dye2 = 576,
        Facewear = 586,
        Name = 590,
        Customize = 622,
    };

    private static readonly SpawnLayout[] SpawnLayouts = { Spawn664, Spawn656 };

    /// <summary>
    /// The layout a PlayerSpawn payload of this length uses, or null when it is a
    /// size no sample has pinned down.  Callers must already have matched the
    /// PlayerSpawn opcode - see <see cref="SpawnLayout"/> for why length alone is
    /// not enough.
    /// </summary>
    public static SpawnLayout? SpawnLayoutFor(int dataLength)
    {
        foreach (var l in SpawnLayouts)
            if (l.Length == dataLength) return l;
        return null;
    }

    /// <summary>Every PlayerSpawn payload length this tool can read.</summary>
    public static IEnumerable<int> KnownSpawnLengths => SpawnLayouts.Select(l => l.Length);

    // Sizes and flags that hold in every known layout.
    public const int DisplayHideHeadgear = 0x40;
    public const int DisplayHideWeapon = 0x80;
    public const int DisplayHideGear = DisplayHideHeadgear | DisplayHideWeapon;

    public const int GearSlots = 10;
    public const int SpawnGearBytes = GearSlots * 4;
    public const int NameBytes = 32;

    // ---- Party portrait ("Party Members" list): 8 members of one stride --------
    // Offsets below were read off real recordings: the customize block is byte-
    // identical to the spawn's, the dye channels line up with the spawn's stains
    // (dye1 verified on every member of every sample, dye2 across 63 blocks).
    // Unlike the spawn packet this one has not moved - the 7.16h recording's
    // portrait blocks match the current offsets field for field.
    public const int PortraitLength = 1408;
    public const int PortraitStride = 176;
    public const int PortraitMembers = PortraitLength / PortraitStride; // 8

    /// <summary>The same per-character key as <see cref="SpawnLayout.CharacterKey"/>.</summary>
    public const int PortraitCharacterKey = 0;
    public const int PortraitJob = 17;
    /// <summary>10 slots of u32 <i>item</i> ids - not models, unlike the spawn packet.</summary>
    public const int PortraitGear = 80;
    public const int PortraitFacewear = 120;
    public const int PortraitCustomize = 124;
    public const int PortraitDye1 = 152;
    public const int PortraitDye2 = 164;

    // ---- PartyList: the party panel's roster ---------------------------------
    //
    // Read off the same eight-player recording, and every offset is pinned by
    // something that identifies itself: the four member names sit exactly 456 bytes
    // apart, the character key at +40 matches that player's PlayerSpawn key, and the
    // world at +80 is the *home* world - the member who is 65/81 in her spawn packet
    // reads 81 here, so this is not simply a copy of whichever world the spawn
    // happened to list first.  There is no current-world field: hers is the only
    // world-valued u16 in her whole 456-byte block.
    //
    // The packet holds eight slots and 3672 is 8*456 + 24, so a 24-byte trailer
    // follows the members; unfilled slots have a zero character key.

    public const int PartyListLength = 3672;
    public const int PartyListStride = 456;
    public const int PartyListMembers = 8;

    public const int PartyListName = 0;
    /// <summary>The same per-character key as <see cref="SpawnLayout.CharacterKey"/>.</summary>
    public const int PartyListCharacterKey = 40;
    /// <summary>Home world only - the spawn packet's <see cref="SpawnLayout.CurrentWorld"/>
    /// has no counterpart here.</summary>
    public const int PartyListHomeWorld = 80;

    // ---- ActorControl: the status icon after the spawn -----------------------
    //
    // The spawn packet is not the last word on a character's status icon: an
    // ActorControl of category 504 carries it again, and a recording holds several.
    // Editing only the spawn leaves the icon correct until the first of these plays
    // and puts the original back.
    //
    // Measured on the ten status recordings: each holds four category-504 packets,
    // and the one that lands a few seconds in matches its own spawn byte in all ten
    // files. The earlier one reads 15 - Viewing Cutscene, the duty load-in - in every
    // recording whose owner had set no status, so these are not copies of the spawn
    // to be trusted individually; they are a timeline, and the whole timeline has to
    // be rewritten for the icon to hold for the length of the playback.
    //
    // Whose status it is comes from the *segment header's* object id, not the
    // payload - which is a different join from every other packet here, and the
    // reason a status edit needs the character's actor id rather than their
    // character key. The PlayerSpawn's own segment header carries that actor id,
    // which is what bridges the two.

    /// <summary>ActorControl category that sets a character's status icon.</summary>
    public const int ActorControlSetStatusIcon = 504;

    /// <summary>u16 category, at the head of every ActorControl payload.</summary>
    public const int ActorControlCategory = 0;

    /// <summary>First u32 argument. For category 504 it is the OnlineStatus row id.</summary>
    public const int ActorControlParam1 = 4;

    /// <summary>Smallest ActorControl payload that has a category and one argument.</summary>
    public const int ActorControlMinLength = 8;

    /// <summary>
    /// The three ActorControl packets, by IPC name. Only the plain one was seen
    /// carrying category 504 - all 40 across the ten recordings - but all three share
    /// the category/argument head and name their subject in the segment header, so
    /// all three are read and rewritten rather than betting the icon on which
    /// variant a future recording happens to use.
    /// </summary>
    public static readonly string[] ActorControlOpNames =
        { "ActorControl", "ActorControlSelf", "ActorControlTarget" };

    /// <summary>Armor slot order, shared by both packets.</summary>
    public static readonly string[] GearSlotNames =
    {
        "Head", "Body", "Hands", "Legs", "Feet",
        "Earrings", "Necklace", "Bracelets", "Ring (R)", "Ring (L)",
    };
}
