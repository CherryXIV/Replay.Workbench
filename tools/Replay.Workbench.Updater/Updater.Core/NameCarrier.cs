using System.Text.Json;

namespace ReplayWorkbench.Updater;

/// <summary>Why one name could not be carried across a hop.</summary>
public sealed record LostName(string Name, int Opcode, string Why);

/// <summary>
/// Names are DERIVED, not downloaded. Carrying the previous patch's table forward
/// through the diff reproduces the published list exactly while keeping the
/// hand-corrections this repo has made — PartyList and PartyPortraitInfo have
/// both been wrong in the third-party dump before.
/// </summary>
public static class NameCarrier
{
    /// <summary>Pseudo-opcodes at or above this are not in the game's IPC vtable.</summary>
    public const int NonIpcOpcode = 0xF000;

    public const string NamesUrl =
        "https://raw.githubusercontent.com/karashiiro/FFXIVOpcodes/refs/heads/master/opcodes.json";

    /// <summary>
    /// Names the workbench looks up rather than just labelling with. If the diff
    /// drops one of these the tool quietly loses a feature, so they get called out.
    /// </summary>
    public static readonly string[] CriticalNames =
    {
        "NpcSpawn", "PlayerSpawn", "PlaceFieldMarker", "PlaceFieldMarkerPreset",
        "PartyList", "PartyPortraitInfo", "FirstAttack", "ActorCast", "Effect",
        "AoeEffect8", "AoeEffect16", "AoeEffect24", "AoeEffect32",
    };

    /// <summary>
    /// Move a patch's name table onto the next patch's opcodes through one hop.
    ///
    /// <para>Pseudo-opcodes (RSVPacket and friends) never appear in a diff; they
    /// are fixed values and carry across as-is.</para>
    /// </summary>
    public static (OpcodeTable Table, List<LostName> Lost) Carry(OpcodeTable table, DiffHop hop)
    {
        var outp = new OpcodeTable();
        var lost = new List<LostName>();
        foreach (var (name, opcode) in table.Entries)
        {
            if (opcode >= NonIpcOpcode) { outp[name] = opcode; continue; }
            if (hop.Map.TryGetValue(opcode, out var moved)) { outp[name] = moved; continue; }
            var why = hop.Ambiguous.Contains(opcode) ? "could not be told apart"
                : hop.Removed.Contains(opcode) ? "was removed"
                : "is absent from the diff";
            lost.Add(new LostName(name, opcode, why));
        }
        return (outp, lost);
    }

    /// <summary>The third-party name dump for <paramref name="patch"/>, or null if it hasn't caught up.</summary>
    public static Dictionary<string, int>? FetchPublished(string patch, Action<string> log, HttpClient http)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(http.GetByteArrayAsync(NamesUrl).GetAwaiter().GetResult());
        }
        catch (Exception e)
        {
            log($"cross-check skipped: could not fetch FFXIVOpcodes ({e.Message})");
            return null;
        }

        using (doc)
        {
            JsonElement? global = null;
            foreach (var block in doc.RootElement.EnumerateArray())
                if (block.TryGetProperty("region", out var r) && r.GetString() == "Global") { global = block; break; }
            if (global is null)
            {
                log("cross-check skipped: no Global region in FFXIVOpcodes");
                return null;
            }
            var version = global.Value.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (version != patch)
            {
                log($"cross-check skipped: FFXIVOpcodes is still on '{version}', not {patch}");
                return null;
            }
            var outp = new Dictionary<string, int>(StringComparer.Ordinal);
            if (global.Value.TryGetProperty("lists", out var lists) &&
                lists.TryGetProperty("ServerZoneIpcType", out var entries) &&
                entries.ValueKind == JsonValueKind.Array)
                foreach (var e in entries.EnumerateArray())
                    if (e.TryGetProperty("name", out var n) && e.TryGetProperty("opcode", out var o))
                        outp[n.GetString()!] = o.GetInt32();
            return outp;
        }
    }

    /// <summary>Report derived-vs-published disagreements. Returns names only they have.</summary>
    public static Dictionary<string, int> CrossCheck(
        OpcodeTable derived, Dictionary<string, int> published, Action<string> log, int limit = 12)
    {
        var shared = derived.Names.Where(published.ContainsKey).ToList();
        var disagree = shared.Where(n => derived[n] != published[n]).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var onlyTheirs = published.Keys.Where(n => !derived.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToDictionary(n => n, n => published[n], StringComparer.Ordinal);
        var onlyOurs = derived.Names.Where(n => !published.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        log($"cross-check: {shared.Count - disagree.Count}/{shared.Count} shared names agree");
        foreach (var name in disagree.Take(limit))
            log($"  differs: {name} derived={derived[name]} published={published[name]}");
        if (disagree.Count > limit) log($"  ... +{disagree.Count - limit} more");
        if (onlyTheirs.Count > 0)
            log($"  {onlyTheirs.Count} name(s) only in the published list: " +
                string.Join(", ", onlyTheirs.Keys.Take(8)) + (onlyTheirs.Count > 8 ? " ..." : ""));
        if (onlyOurs.Count > 0)
            log($"  {onlyOurs.Count} name(s) only in ours: " +
                string.Join(", ", onlyOurs.Take(8)) + (onlyOurs.Count > 8 ? " ..." : ""));
        return onlyTheirs;
    }
}
