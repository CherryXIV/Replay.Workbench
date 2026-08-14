using System.Text;

namespace ReplayWorkbench.Updater;

/// <summary>Everything the update can be told to do; mirrors update_patch.py's flags.</summary>
public sealed class UpdateOptions
{
    /// <summary>Game build number of the new patch (int32 at 0x10 of a .dat).</summary>
    public int? Build { get; set; }
    /// <summary>Read the build number out of a recording made on the new patch.</summary>
    public string? FromReplay { get; set; }
    /// <summary>Stop at this patch instead of the newest diff published.</summary>
    public string? To { get; set; }
    /// <summary>A local opcodediff diffs/ to mirror from instead of downloading.</summary>
    public string? DiffsDir { get; set; }
    /// <summary>Report what would change, write nothing.</summary>
    public bool Check { get; set; }
    /// <summary>Leave docs/old/opcodes.js alone.</summary>
    public bool NoOld { get; set; }
    /// <summary>Cross-check derived names against FFXIVOpcodes.</summary>
    public bool VerifyNames { get; set; } = true;
    /// <summary>Also adopt names FFXIVOpcodes has that the diff could not carry forward.</summary>
    public bool MergeNewNames { get; set; }
    /// <summary>Regenerate the desktop core's embedded JSON afterwards.</summary>
    public bool ExportCoreData { get; set; } = true;
}

public sealed class UpdateResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    /// <summary>The chain tail after the run — the patch everything is now on.</summary>
    public string Tail { get; init; } = "";
    public List<string> Added { get; init; } = new();
    public bool Wrote { get; init; }
}

/// <summary>
/// One run of the patch update: mirror the diffs, extend the chain, regenerate
/// docs/patchdiffs.js, derive the new name table, and (given a build number) bump
/// LATEST_PATCH / LATEST_GAME_BUILD.
///
/// <para>Every step is idempotent and says "already current" when there is
/// nothing to do, because the two halves of an update usually arrive on different
/// days: opcodediff publishes the diff within hours of a patch, but the build
/// number can only be read out of a recording made on the new client.</para>
///
/// <para>Port of tools/update_patch.py, which stays in the tree as the oracle the
/// port is diffed against.</para>
/// </summary>
public static class UpdateRunner
{
    public static UpdateResult Run(string repoRoot, UpdateOptions o, Action<string> log)
    {
        try { return RunCore(repoRoot, o, log); }
        catch (FatalException e)
        {
            log($"error: {e.Message}");
            return new UpdateResult { Ok = false, Error = e.Message };
        }
    }

    private static UpdateResult RunCore(string repoRoot, UpdateOptions o, Action<string> log)
    {
        var tools = Path.Combine(repoRoot, "tools");
        var mirrorDir = Path.Combine(tools, "diffs");
        var opcodesJsPath = Path.Combine(repoRoot, "docs", "opcodes.js");
        var oldOpcodesJsPath = Path.Combine(repoRoot, "docs", "old", "opcodes.js");
        var buildsJsonPath = Path.Combine(tools, "replay_builds.json");
        var buildPatchdiffsPy = Path.Combine(tools, "build_patchdiffs.py");
        var bumpReplayPy = Path.Combine(tools, "bump_replay.py");
        var patchdiffsJsPath = Path.Combine(repoRoot, "docs", "patchdiffs.js");

        void Section(string title) => log($"\n== {title} ==");

        var dry = o.Check;
        if (dry) log("preview: nothing will be written (the diff mirror is still filled)");

        using var http = DiffMirror.NewClient();

        // ---- build number -------------------------------------------------
        int? build = o.Build;
        Dictionary<int, int>? hist = null;
        if (!string.IsNullOrWhiteSpace(o.FromReplay))
        {
            var probe = ReplayProbe.Read(o.FromReplay!);
            hist = probe.Histogram;
            log($"{Path.GetFileName(o.FromReplay)}: build {probe.Build}, {probe.Packets:N0} packets, " +
                $"{probe.Histogram.Count} opcode kinds");
            if (build is not null && build != probe.Build)
                throw new FatalException($"build {build} disagrees with {o.FromReplay} (build {probe.Build})");
            build = probe.Build;
        }

        // ---- 1. diffs -----------------------------------------------------
        Section("diffs");
        var chain = ChainFile.Read(buildPatchdiffsPy, "VERSION_CHAIN");
        if (chain.Count == 0) throw new FatalException("VERSION_CHAIN is empty");
        log($"mirror: {Path.GetRelativePath(repoRoot, mirrorDir)}");

        var local = o.DiffsDir;
        if (!string.IsNullOrWhiteSpace(local) && !Directory.Exists(local))
            throw new FatalException($"diffs directory {local} is not a directory");
        if (string.IsNullOrWhiteSpace(local))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(repoRoot)!, "opcodediff", "diffs");
            local = Directory.Exists(sibling) ? sibling : null;
        }
        var available = DiffMirror.Sync(mirrorDir, chain[0], local, log, http);

        // ---- 2. chain -----------------------------------------------------
        Section("chain");
        var newest = PatchVersion.Newest(available);
        var ceiling = o.To ?? newest;
        if (o.To is not null && !available.Contains(o.To))
            throw new FatalException($"stop-at {o.To}: no {o.To}.diff.json published yet");

        var additions = available
            .Where(v => PatchVersion.Compare(chain[^1], v) < 0 && PatchVersion.Compare(v, ceiling) <= 0)
            .ToList();

        // An expansion replaces the engine, so a recording carried across that
        // boundary is not a recording of the new client no matter how well the
        // opcodes line up. Stop at the boundary unless told otherwise.
        var major = PatchVersion.Of(chain[^1]).Major;
        var crossing = additions.Where(v => PatchVersion.Of(v).Major != major).ToList();
        if (crossing.Count > 0 && o.To is null)
        {
            log($"stopping short of {crossing[0]}: that is a new expansion, and the chain " +
                "deliberately does not cross one (see VERSION_CHAIN in build_patchdiffs.py).");
            log($"    set stop-at {crossing[^1]} to chain across it anyway.");
            additions = additions.Where(v => !crossing.Contains(v)).ToList();
        }

        if (additions.Count == 0)
        {
            log($"chain is already current at {chain[^1]} (newest diff published: {newest})");
        }
        else
        {
            log($"new patch(es): {string.Join(", ", additions)}");
            var previous = chain[^1];
            foreach (var version in additions)
            {
                log($"  {version}: {DiffHop.Alignment(mirrorDir, previous, version)}");
                previous = version;
            }
            chain = chain.Concat(additions).ToList();
            if (!dry)
            {
                ChainFile.Extend(buildPatchdiffsPy, "VERSION_CHAIN", additions);
                ChainFile.Extend(bumpReplayPy, "FALLBACK_CHAIN", additions);
                log("VERSION_CHAIN and FALLBACK_CHAIN extended");
            }
        }

        var tail = chain[^1];
        if (!dry)
        {
            var (hops, warnings) = PatchDiffsWriter.Build(chain, mirrorDir);
            ChainFile.WriteText(patchdiffsJsPath, PatchDiffsWriter.Render(chain, hops));
            log($"regenerated docs/patchdiffs.js ({hops.Count} hops, {chain[0]} -> {chain[^1]})");
            foreach (var w in warnings) log("note:" + w);
        }

        // ---- 3. names -----------------------------------------------------
        Section("names");
        var js = ChainFile.ReadText(opcodesJsPath);
        var tables = OpcodesJs.ReadTables(js);
        // Names are only ever carried forward from the newest table already pasted
        // in, so a re-run finds nothing to do and the hand-corrections in that table
        // keep propagating instead of being overwritten by a fresh derivation.
        var populated = tables.Where(t => t.Table.Count > 0).ToList();
        if (populated.Count == 0) throw new FatalException("OPCODE_TABLES has no populated table to carry forward");
        var source = populated.OrderBy(t => PatchVersion.Of(t.Patch)).Last();
        var missing = chain.Where(v => PatchVersion.Compare(v, source.Patch) > 0).ToList();

        var newTables = new Dictionary<string, OpcodeTable>(StringComparer.Ordinal);
        if (missing.Count == 0)
        {
            log($"OPCODE_TABLES already has an entry for {tail}");
        }
        else
        {
            var table = source.Table;
            log($"carrying {table.Count} names forward from {source.Patch}");
            foreach (var version in missing)
            {
                var (carried, lost) = NameCarrier.Carry(table, DiffHop.Load(mirrorDir, version));
                table = carried;
                newTables[version] = carried;
                log($"  {version}: {carried.Count} names ({lost.Count} lost)");
                foreach (var l in lost)
                {
                    var mark = NameCarrier.CriticalNames.Contains(l.Name) ? "  !! " : "     ";
                    log($"{mark}{l.Name} ({l.Opcode}) {l.Why}");
                }
                if (lost.Any(l => NameCarrier.CriticalNames.Contains(l.Name)))
                    log("  !! the workbench looks those up by name -- fix them by hand before shipping");

                var dupes = carried.Collisions();
                if (dupes.Count > 0)
                {
                    log($"  !! {dupes.Count} opcode(s) carry two names; transpose refuses tables like this:");
                    foreach (var (opcode, names) in dupes.Take(4))
                        log($"     {opcode} = {string.Join(" + ", names)}");
                }
            }

            if (o.VerifyNames)
            {
                var published = NameCarrier.FetchPublished(tail, log, http);
                if (published is not null)
                {
                    var onlyTheirs = NameCarrier.CrossCheck(newTables[tail], published, log);
                    if (onlyTheirs.Count > 0 && o.MergeNewNames)
                    {
                        var taken = new HashSet<int>(newTables[tail].Opcodes);
                        var added = 0;
                        foreach (var (name, opcode) in onlyTheirs)
                        {
                            if (taken.Contains(opcode)) continue;
                            newTables[tail][name] = opcode;
                            added++;
                        }
                        log($"  merged {added} published name(s) the diff could not carry forward");
                    }
                }
            }

            if (!dry)
            {
                foreach (var version in missing) js = OpcodesJs.InsertTable(js, version, newTables[version]);
                log($"docs/opcodes.js: added OPCODE_TABLES entries for {string.Join(", ", missing)}");
            }
        }

        // ---- 4. build number ----------------------------------------------
        Section("build");
        if (build is null)
        {
            log($"no build number given, so LATEST_PATCH stays at {OpcodesJs.ReadLatest(js)}.");
            log("Record a replay on the new client and run again with the build number");
            log("(or point it at that recording). Bumping LATEST_GAME_BUILD without it would");
            log("stamp exports with the old build, and the client refuses to load those.");
        }
        else
        {
            log($"build {build} -> {tail}");
            if (!dry)
            {
                js = OpcodesJs.SetBuildToPatch(js, build.Value, tail);
                js = OpcodesJs.SetLatest(js, tail, build.Value);
                BuildsJson.Add(buildsJsonPath, build.Value, tail, log);
            }
        }

        if (!dry)
        {
            ChainFile.WriteText(opcodesJsPath, js);
            log("docs/opcodes.js written");
            if (!o.NoOld && File.Exists(oldOpcodesJsPath))
                UpdateOldOpcodes(oldOpcodesJsPath, newTables, tail, build, log);
        }

        // ---- 5. the desktop core's embedded copy --------------------------
        if (!dry && o.ExportCoreData)
        {
            Section("core data");
            CoreDataExporter.Export(repoRoot, log);
        }

        // ---- 6. confirmation ----------------------------------------------
        if (hist is not null)
        {
            Section("verify");
            ReplayProbe.ConfirmAgainstPatch(hist, mirrorDir, tail, log);
        }

        Section("next");
        if (dry)
        {
            log("this was a preview -- apply to write the changes.");
            return new UpdateResult { Ok = true, Tail = tail, Added = additions, Wrote = false };
        }
        log("PartyList and PartyPortraitInfo have been wrong in the published list before;");
        log($"confirm them against a real {tail} recording:");
        log("    python tools/find_partylist_opcode.py \"some 7.x recording.dat\"");
        log("Then check the site loads a recording and reports the right patch.");
        return new UpdateResult { Ok = true, Tail = tail, Added = additions, Wrote = true };
    }

    /// <summary>
    /// docs/old/ is the frozen name-only build of the tool; it transposes by IPC
    /// name alone, so it needs the same table to keep working on a new patch.
    /// </summary>
    private static void UpdateOldOpcodes(
        string path, Dictionary<string, OpcodeTable> newTables, string patch, int? build, Action<string> log)
    {
        var text = ChainFile.ReadText(path);
        var existing = OpcodesJs.ReadTables(text).Select(t => t.Patch).ToHashSet(StringComparer.Ordinal);
        foreach (var (version, table) in newTables)
            if (!existing.Contains(version))
                text = OpcodesJs.InsertTable(text, version, table);
        if (build is not null)
        {
            text = OpcodesJs.SetBuildToPatch(text, build.Value, patch);
            text = OpcodesJs.SetLatest(text, patch, build.Value);
        }
        ChainFile.WriteText(path, text);
        log("docs/old/opcodes.js updated");
    }
}
