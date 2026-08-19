namespace ReplayWorkbench.Core;

/// <summary>One DataSegment: a 12-byte header plus its payload.</summary>
public sealed class Segment
{
    /// <summary>Byte offset within the data area (i.e. relative to DataStart).</summary>
    public required int Offset { get; init; }
    public required int Opcode { get; init; }
    public required int DataLength { get; init; }
    public required uint Ms { get; init; }
    public required uint Oid { get; init; }

    /// <summary>Header + payload.</summary>
    public int Total => ReplayFormat.SegHeader + DataLength;
}

/// <summary>One chapter-array entry.</summary>
public sealed class Chapter
{
    public required int Type { get; init; }
    public required uint Offset { get; init; }
    public required uint Ms { get; init; }

    public string TypeName =>
        ReplayFormat.ChapterTypeNames.TryGetValue(Type, out var n) ? n : Type.ToString();
}

/// <summary>A pull: a Start/Restart (or Barrier Down) chapter plus its computed range.</summary>
public sealed class Pull
{
    public required Chapter Chapter { get; init; }
    /// <summary>1-based pull number, as shown in the UI.</summary>
    public required int Number { get; init; }
    public required int StartIndex { get; init; }
    public required int EndIndex { get; init; }
    public required uint LengthMs { get; init; }
    /// <summary>Segment index where this pull's actor respawn batch begins.</summary>
    public required int RespawnStart { get; init; }
    public required int BatchCount { get; init; }
    public required uint CombatMs { get; init; }
    /// <summary>The engage chapter for this pull, or null when it has none.</summary>
    public required Chapter? Countdown { get; init; }
    public required int CountdownIndex { get; init; }
}

/// <summary>One person in the recording: the name they carry and every place it occurs.</summary>
public sealed class PlayerName
{
    public required string Name { get; init; }
    public required IReadOnlyList<int> Offsets { get; init; }

    /// <summary>
    /// The PlayerSpawn character key this person was read from, or 0 when the name
    /// was only found by scanning and no spawn packet describes them.  Two people
    /// can carry the same name, so this - not the name - is what identifies them.
    /// </summary>
    public ulong CharacterKey { get; init; }

    /// <summary>What the user typed, or null when untouched.</summary>
    public string? NewName { get; set; }

    /// <summary>The name that will be written on export.</summary>
    public string Effective => NewName ?? Name;
}

/// <summary>What <see cref="PatchChain.DetectPatch"/> made of a file's opcodes.</summary>
public sealed record PatchDetection(
    string Patch,
    double Packets,
    double Kinds,
    string? RunnerUp,
    double Margin,
    bool Confident);

/// <summary>One hop in the patch chain: which opcode became which, plus what fell out.</summary>
public sealed class PatchHop
{
    public required string Version { get; init; }
    public required IReadOnlyDictionary<int, int> Map { get; init; }
    /// <summary>Opcodes the diff saw on the old side but could not carry forward.</summary>
    public required IReadOnlySet<int> Lost { get; init; }
}

/// <summary>The composed result of walking the chain from one patch to another.</summary>
public sealed class PatchChainMap
{
    public required IReadOnlyDictionary<int, int> Map { get; init; }
    /// <summary>Opcode to the reason it could not be carried the whole way.</summary>
    public required IReadOnlyDictionary<int, string> Lost { get; init; }
}

/// <summary>How to get from one patch's opcodes to another's, or why we can't.</summary>
public sealed class RemapPlan
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    /// <summary>"diffs" or "names" - which source answered.</summary>
    public string? Via { get; init; }
    public IReadOnlyDictionary<int, int> Map { get; init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, string> Lost { get; init; } = new Dictionary<int, string>();

    public static RemapPlan Fail(string reason) => new() { Ok = false, Reason = reason };
}

/// <summary>Outcome of rewriting a finished export's opcodes onto another patch.</summary>
public sealed class TransposeResult
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Via { get; init; }
    public int Rewritten { get; init; }
    public int SegTotal { get; init; }
    public int UnknownSegs { get; init; }
    public int UnknownKinds { get; init; }

    public static TransposeResult Fail(string reason) => new() { Ok = false, Reason = reason };
}

/// <summary>Artifact gear for one job (from afgear.json).</summary>
public sealed class JobGear
{
    /// <summary>Item ids for the portrait/appearance packet, slot order:
    /// head, chest, hands, legs, feet, earrings, neck, wrist, ringR, ringL.</summary>
    public required int[] Gear { get; init; }
    /// <summary>[model, variant] per armor slot, for the in-arena PlayerSpawn.</summary>
    public required int[][] GearModels { get; init; }
    /// <summary>Mainhand [model, base, variant].</summary>
    public required int[]? WeaponModel { get; init; }
    /// <summary>Offhand/secondary [model, base, variant], or null for no offhand.</summary>
    public required int[]? WeaponSub { get; init; }
}

/// <summary>Options for <see cref="PullExporter.BuildPull"/>.</summary>
public sealed class ExportOptions
{
    /// <summary>Carry the last waymarks into the pull.</summary>
    public bool Waymarks { get; set; } = true;
    /// <summary>Write the edited player names.</summary>
    public bool ApplyNames { get; set; } = true;
    /// <summary>Keep the engage (countdown) chapter as a second chapter entry.</summary>
    public bool Countdown { get; set; } = true;
}
