namespace ReplayWorkbench.Core;

/// <summary>
/// FFXIVReplay .dat layout constants (from the FFXIVReplay struct).
///
///   Header        : 0x68 bytes
///   ChapterArray  : 0x4 + 0xC*64 bytes
///   Chapter       : int type; uint offset; uint ms      (0xC bytes)
///   data starts at: 0x68 + 0x304 = 0x36C
///   DataSegment   : u16 opcode; u16 dataLength; u32 ms; u32 objectID (12) + payload
/// </summary>
public static class ReplayFormat
{
    public const int HeaderSize = 0x68;
    public const int ChapterEntry = 0xC;
    public const int MaxChapters = 64;
    public const int ChapterArray = 0x4 + ChapterEntry * MaxChapters; // 0x304
    public const int DataStart = HeaderSize + ChapterArray;           // 0x36C
    public const int SegHeader = 12;

    public const int OffVersion = 0x0C;
    public const int OffOsType = 0x0E;
    public const int OffBuild = 0x10;
    public const int OffTimestamp = 0x14;
    public const int OffTotalMs = 0x18;
    public const int OffDisplayedMs = 0x1C;
    public const int OffContentId = 0x20;
    public const int OffInfo = 0x28;
    public const int OffLocalCid = 0x30;
    public const int OffJobs = 0x38;
    public const int OffPlayerIndex = 0x40;
    public const int OffReplayLen = 0x48;

    /// <summary>Chapter types that open a pull.</summary>
    public static readonly int[] PullStartTypes = { 2, 5 };

    /// <summary>Chapter types that mark a countdown (1 = Countdown, 3 = Countdown(3)).</summary>
    public static readonly int[] CountdownChapterTypes = { 1, 3 };

    /// <summary>
    /// The director packet has no named entry in FFXIVOpcodes, so unlike the
    /// spawn/waymark opcodes it stays a fixed fallback rather than being
    /// resolved per patch.
    /// </summary>
    public const int DirectorOpcode = 0x03E4;

    // Defaults used when the file's patch can't be identified: the latest
    // patch's values, matching the browser tool.
    public const int DefaultSpawnOpcode = 0x0113;         // NpcSpawn
    public const int DefaultWaymarkOpcode = 0x0255;       // PlaceFieldMarker
    public const int DefaultWaymarkPresetOpcode = 0x02AB; // PlaceFieldMarkerPreset

    /// <summary>Opcodes at 0xf000 and up are replay control markers, not IPC.</summary>
    public const int IpcOpcodeCeiling = 0xf000;

    /// <summary>
    /// Real combat is continuous (an action every couple seconds), so combat
    /// actions are split into clusters separated by idle gaps longer than this.
    /// A tiny trailing cluster (fewer than <see cref="CombatMinCluster"/>
    /// actions) after such a gap is post-fight noise - a DoT tick or stray cast
    /// seconds after the boss died / the party wiped - and is trimmed.
    /// </summary>
    public const uint CombatGapMs = 10000;
    public const int CombatMinCluster = 8;

    public const int BatchLookback = 8000;
    public const uint BatchMsWindow = 2000;
    public const int MinBatchSpawns = 20;

    public static ReadOnlySpan<byte> Magic => "FFXIVREPLAY\0"u8;

    public static readonly IReadOnlyDictionary<int, string> ChapterTypeNames = new Dictionary<int, string>
    {
        [1] = "Countdown",
        [2] = "Start/Restart",
        [3] = "Countdown(3)",
        [4] = "Event Cutscene",
        [5] = "Barrier Down",
    };

    /// <summary>A small, partial Job-ID to abbreviation map, for display only.</summary>
    public static readonly IReadOnlyDictionary<int, string> JobAbbr = new Dictionary<int, string>
    {
        [0] = "-", [1] = "GLA", [2] = "PGL", [3] = "MRD", [4] = "LNC", [5] = "ARC", [6] = "CNJ", [7] = "THM",
        [19] = "PLD", [20] = "MNK", [21] = "WAR", [22] = "DRG", [23] = "BRD", [24] = "WHM", [25] = "BLM",
        [26] = "ACN", [27] = "SMN", [28] = "SCH", [29] = "ROG", [30] = "NIN", [31] = "MCH", [32] = "DRK",
        [33] = "AST", [34] = "SAM", [35] = "RDM", [36] = "BLU", [37] = "GNB", [38] = "DNC", [39] = "RPR",
        [40] = "SGE", [41] = "VPR", [42] = "PCT",
    };

    /// <summary>Packets whose presence marks a combat action (resolved per patch).</summary>
    public static readonly string[] CombatOpNames =
        { "ActorCast", "Effect", "AoeEffect8", "AoeEffect16", "AoeEffect24", "AoeEffect32" };
}
