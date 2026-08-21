using System.Text;
using ReplayWorkbench.Core;

// Post-update validation. Run after a patch update has regenerated the data and
// the solution has been rebuilt, so it tests what was actually just written.
//
//   Replay.Workbench.Verify [recording.dat ...]
//
// Exits non-zero if any check fails, so the updater can stop on it.

Console.OutputEncoding = new UTF8Encoding(false);

int checks = 0, failures = 0;
void Check(bool ok, string what, string detail = "")
{
    checks++;
    if (ok) { Console.WriteLine($"  ok    {what}"); return; }
    failures++;
    Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? "  -- " + detail : "")}");
}

Console.WriteLine($"embedded data: {OpcodeData.Chain.Count} patches, " +
                  $"latest {OpcodeData.LatestPatch} (build {OpcodeData.LatestGameBuild})");

// ---- 1. the latest patch must have a usable name table ---------------------
Console.WriteLine("\nlatest patch table");
var latest = PatchChain.PatchTable(OpcodeData.LatestPatch);
Check(latest is { Count: > 0 }, "the latest patch resolves to a name table",
    latest is null ? "no table at all" : $"{latest.Count} names");

if (latest is { Count: > 0 })
{
    // The one that crashes clients: two packet types sharing an opcode means
    // transpose collapses them and the game reads one with the other's struct.
    var cols = Transpose.OpcodeCollisions(latest);
    Check(cols.Count == 0, "no opcode carries two packet names",
        cols.Count == 0 ? "" : Transpose.DescribeCollisions(cols, 4));

    foreach (var name in ReplayFormat.CombatOpNames.Concat(new[]
             {
                 "NpcSpawn", "PlayerSpawn", "PlaceFieldMarker", "PlaceFieldMarkerPreset",
                 "PartyList", "PartyPortraitInfo", "FirstAttack", "ModelEquip",
             }))
        Check(latest.ContainsKey(name), $"the table still has {name}");
}

// ---- 2. the chain must reach the latest patch from the oldest --------------
Console.WriteLine("\npatch chain");
var oldest = OpcodeData.Chain[0];
var plan = Transpose.Plan(oldest, OpcodeData.LatestPatch);
Check(plan.Ok, $"a {oldest} recording can still be remapped to {OpcodeData.LatestPatch}", plan.Reason ?? "");
if (plan.Ok) Console.WriteLine($"        via {plan.Via}, {plan.Map.Count} opcodes carried, {plan.Lost.Count} lost");

Check(OpcodeData.BuildToPatch.Values.Contains(OpcodeData.LatestPatch),
    "some build number maps to the latest patch",
    "BUILD_TO_PATCH has no entry for it, so a fresh recording falls back to detection");

// ---- 3. real recordings, if any were given --------------------------------
foreach (var path in args)
{
    Console.WriteLine($"\n{Path.GetFileName(path)}");
    ReplayFile file;
    try { file = ReplayFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path)); }
    catch (Exception e) { Check(false, "parses", e.Message); continue; }

    Check(true, $"parses ({file.Segments.Count:N0} segments, {file.Pulls.Count} pulls)");

    var det = file.PatchDetected;
    Check(det is not null, "its patch can be detected from its own opcodes");
    if (det is not null)
        Console.WriteLine($"        {det.Patch} at {det.Packets * 100:0.00}% of packets / " +
                          $"{det.Kinds * 100:0.00}% of kinds, next best {det.RunnerUp}" +
                          (det.Confident ? " (confident)" : " (NOT confident)"));

    // A recording on the newest patch should be accounted for exactly.
    if (file.FileBuild == OpcodeData.LatestGameBuild)
    {
        Check(det is { Confident: true }, $"a build-{OpcodeData.LatestGameBuild} recording is confidently identified");
        Check(det?.Patch == OpcodeData.LatestPatch,
            $"it is identified as {OpcodeData.LatestPatch}", $"got {det?.Patch}");
    }

    Check(file.Players.Count > 0, "players were found");
    if (file.Pulls.Count > 0)
    {
        try
        {
            var ex = PullExporter.BuildPull(file, 0, new ExportOptions());
            var round = ReplayFile.Parse(ex.Bytes, "pull");
            Check(round.Segments.Count > 0, "pull 1 exports and re-parses",
                $"{ex.Bytes.Length:N0} bytes");
        }
        catch (Exception e) { Check(false, "pull 1 exports and re-parses", e.Message); }
    }

    if (file.FilePatch is not null && file.FilePatch != OpcodeData.LatestPatch)
    {
        var forward = Transpose.Plan(file.FilePatch, OpcodeData.LatestPatch);
        Check(forward.Ok, $"it can be transposed {file.FilePatch} -> {OpcodeData.LatestPatch}", forward.Reason ?? "");
    }
}

if (args.Length == 0)
    Console.WriteLine("\nno recording given -- pass one made on the new patch for the full check.");

Console.WriteLine($"\n{checks} checks, {failures} failure(s)");
return failures == 0 ? 0 : 1;
