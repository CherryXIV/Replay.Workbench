using System.Reflection;
using System.Text.Json;

namespace ReplayWorkbench.Core;

/// <summary>
/// The two opcode data sources, loaded from the JSON embedded at build time by
/// tools/export_core_data.py.  They have different jobs:
///
/// <para><b>patchdiffs</b> (PATCH_CHAIN / PATCH_DIFFS) records which opcode number
/// became which at each game patch, read out of the binary's IPC vtable.
/// Transpose runs on this and nothing else: it is exact, it covers every
/// Dawntrail patch, and it never needs to know what a packet is called.</para>
///
/// <para><b>opcodes</b> (OPCODE_TABLES) holds IPC <i>names</i>, for the inspector's
/// labels and for the handful of packets this tool looks up by name (NpcSpawn,
/// PlaceFieldMarker, PartyPortraitInfo, the combat-timing set).  Names come from
/// a third-party dump that lags patches and has been wrong before, so they label
/// packets; they do not decide how packets get rewritten.</para>
///
/// Only the latest patch needs a real name table.  Every older patch's names are
/// projected backwards from it through the chain (<see cref="PatchChain.PatchTable"/>).
/// </summary>
public static class OpcodeData
{
    private const string PackAlphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_";

    private static readonly Dictionary<char, int> PackIndex =
        PackAlphabet.Select((c, i) => (c, i)).ToDictionary(t => t.c, t => t.i);

    private static readonly Dictionary<string, Dictionary<string, int>> _tables = new();
    private static readonly Dictionary<int, string> _buildToPatch = new();
    private static readonly Dictionary<string, PackedHop> _packedDiffs = new();
    private static readonly Dictionary<int, JobGear> _jobGear = new();
    private static readonly List<string> _chain = new();
    private static readonly Dictionary<string, int> _chainPos = new();

    /// <summary>The patch transpose targets. Mutable: a runtime-registered table becomes latest.</summary>
    public static string LatestPatch { get; private set; } = "";

    /// <summary>The build a transposed export is re-stamped to.</summary>
    public static int LatestGameBuild { get; private set; }

    public static IReadOnlyList<string> Chain => _chain;
    public static IReadOnlyDictionary<int, string> BuildToPatch => _buildToPatch;

    /// <summary>Raised when a table is registered, so caches downstream can drop.</summary>
    public static event Action? Changed;

    static OpcodeData()
    {
        using (var doc = JsonDocument.Parse(ReadResource("opcodes.json")))
        {
            var root = doc.RootElement;
            LatestPatch = root.GetProperty("latestPatch").GetString()!;
            LatestGameBuild = root.GetProperty("latestGameBuild").GetInt32();
            foreach (var e in root.GetProperty("buildToPatch").EnumerateObject())
                _buildToPatch[int.Parse(e.Name)] = e.Value.GetString()!;
            foreach (var e in root.GetProperty("opcodeTables").EnumerateObject())
            {
                var table = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var op in e.Value.EnumerateObject()) table[op.Name] = op.Value.GetInt32();
                _tables[e.Name] = table;
            }
        }

        using (var doc = JsonDocument.Parse(ReadResource("patchdiffs.json")))
        {
            var root = doc.RootElement;
            foreach (var p in root.GetProperty("patchChain").EnumerateArray())
                _chain.Add(p.GetString()!);
            for (var i = 0; i < _chain.Count; i++) _chainPos[_chain[i]] = i;
            foreach (var e in root.GetProperty("patchDiffs").EnumerateObject())
                _packedDiffs[e.Name] = new PackedHop(
                    Str(e.Value, "o"), Str(e.Value, "n"), Str(e.Value, "a"), Str(e.Value, "r"));
        }

        using (var doc = JsonDocument.Parse(ReadResource("afgear.json")))
        {
            foreach (var e in doc.RootElement.EnumerateObject())
                _jobGear[int.Parse(e.Name)] = new JobGear
                {
                    Gear = Ints(e.Value.GetProperty("gear")),
                    GearModels = e.Value.GetProperty("gearModels").EnumerateArray().Select(Ints).ToArray(),
                    WeaponModel = OptInts(e.Value, "weaponModel"),
                    WeaponSub = OptInts(e.Value, "weaponSub"),
                };
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();
        static int[]? OptInts(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? Ints(v) : null;
    }

    private static byte[] ReadResource(string file)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = $"{typeof(OpcodeData).Namespace}.Data.{file}";
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource {name} is missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Unpack a packed opcode list (two alphabet chars per opcode) into numbers.</summary>
    public static int[] UnpackOpcodes(string s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<int>();
        var outp = new int[s.Length >> 1];
        for (int i = 0, j = 0; i + 1 < s.Length; i += 2, j++)
            outp[j] = PackIndex[s[i]] * 64 + PackIndex[s[i + 1]];
        return outp;
    }

    public static bool InChain(string? patch) => patch is not null && _chainPos.ContainsKey(patch);
    public static int ChainPos(string patch) => _chainPos[patch];

    internal static PackedHop? PackedDiff(string version) =>
        _packedDiffs.TryGetValue(version, out var h) ? h : null;

    /// <summary>The hand-pasted name table for a patch, or null when there isn't one.</summary>
    public static IReadOnlyDictionary<string, int>? RawTable(string? patch) =>
        patch is not null && _tables.TryGetValue(patch, out var t) ? t : null;

    /// <summary>
    /// A pasted table only counts if it actually has entries. An empty one is a
    /// placeholder for a build registered without names - that should fall
    /// through to derivation, not report zero names.
    /// </summary>
    public static bool HasNames(IReadOnlyDictionary<string, int>? t) => t is { Count: > 0 };

    public static JobGear? GearForJob(int job) => _jobGear.TryGetValue(job, out var g) ? g : null;

    /// <summary>
    /// Register an opcode table for a build at runtime (the browser tool's dev
    /// menu), so a new game patch can be tested before it is baked into the data
    /// files.  The table is promoted to latest, because applying it is the same
    /// as saying "this is what current is now": transpose targets it and the
    /// build re-stamp uses it.  Nothing is persisted.
    /// </summary>
    /// <returns>The synthetic patch key the table was registered under.</returns>
    public static string RegisterTable(int build, IReadOnlyDictionary<string, int> table)
    {
        if (build <= 0) throw new ArgumentOutOfRangeException(nameof(build), "build must be positive");
        var cols = Transpose.OpcodeCollisions(table);
        if (cols.Count > 0)
            throw new InvalidOperationException(
                $"this table gives one opcode two packet names ({Transpose.DescribeCollisions(cols)}). " +
                "Transpose maps packets by name, so those two packet types would collapse onto a single " +
                "opcode and the client would crash reading one as the other. Fix the duplicate and re-apply.");

        var key = "Custom-" + build;
        _tables[key] = new Dictionary<string, int>(table, StringComparer.Ordinal);
        _buildToPatch[build] = key;
        LatestPatch = key;
        LatestGameBuild = build;
        Changed?.Invoke();
        return key;
    }

    internal sealed record PackedHop(string O, string N, string A, string R);
}
