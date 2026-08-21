using ReplayWorkbench.Core;

namespace ReplayWorkbench.App;

/// <summary>
/// The editor: load a duty recording, read its header, pick a pull off the
/// timeline, and export that pull as a standalone .dat.  A desktop port of the
/// Editor tab of docs/index.html; all of the actual work lives in
/// Replay.Workbench.Core.
/// </summary>
/// <remarks>
/// Every control, and where it sits, lives in MainForm.Designer.cs - open the form
/// in the WinForms designer to move things around.  This file holds behaviour only:
/// what the controls do once something is loaded into them.  The cards stack by
/// Dock=Top inside <c>content</c>, so their order and spacing are designer
/// properties (Margin) rather than arithmetic.
/// </remarks>
internal sealed partial class MainForm : Form
{
    /// <summary>An InitZone payload lifted from a current-layout recording, for
    /// rebuilding an old one. Set from Tools; kept for the life of the process.</summary>
    private byte[]? _initZoneTemplate;
    private string? _initZoneTemplateFrom;

    /// <summary>Characters with a PlayerSpawn in the loaded file.</summary>
    private IReadOnlyList<CharacterRecord> _characters = Array.Empty<CharacterRecord>();

    /// <summary>Pending per-character looks, keyed by the character key so they survive a
    /// re-parse (picking a patch by hand rebuilds the file but not the people).</summary>
    private readonly Dictionary<ulong, CharacterAppearance> _charEdits = new();

    /// <summary>The stacking order <c>content</c>'s children were laid out in, captured
    /// straight after InitializeComponent so the designer stays the one place it is
    /// decided.  Docked children stack by z-order, and showing a hidden one shuffles
    /// that, so it has to be put back.</summary>
    private readonly Control[] _contentOrder;

    /// <summary>The pull table's column widths as the designer left them, at 96dpi.
    /// <see cref="SizeColumns"/> scales these instead of hard-coding pixels, so
    /// widening a column in the designer is a change that actually shows up.</summary>
    private readonly int[] _baseColumnWidths;

    private ReplayFile? _file;
    private byte[]? _rawBytes;
    private string _path = "";
    /// <summary>The patch the user picked by hand, if any - survives a re-parse.</summary>
    private string? _patchOverride;
    private int _selectedPull = -1;
    private bool _suppressPatchEvent;

    public MainForm(string? openOnStart = null)
    {
        InitializeComponent();
        _contentOrder = content.Controls.Cast<Control>().ToArray();

        _baseColumnWidths = pullList.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
        RightAlignFirstColumn();
        pullList.HandleCreated += (_, _) => SizeColumns();
        pullList.Resize += (_, _) => SizeColumns();

        // The cards are laid out in the designer so they can be seen there, but the
        // app opens on an empty window: nothing to show until a file is dropped.
        foreach (var c in Cards()) c.Visible = false;

        foreach (var (_, name) in Customize.Races) racePick.Items.Add(name);
        racePick.SelectedIndex = 0;
        optAnon.Box.CheckedChanged += (_, _) => SyncRaceEnabled();

        DragEnter += (_, e) => e.Effect = DroppedPath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { var p = DroppedPath(e); if (p is not null) LoadRecording(p); };

        Say("Ready. Drop a .dat on the window to begin.");

        // Wait for the window to exist: loading relayouts against real client sizes.
        if (openOnStart is not null)
            Shown += (_, _) => LoadRecording(openOnStart);

    }


    /// <summary>
    /// Re-add the pull table's first column through the overload that takes an
    /// alignment.  Win32 left-aligns column 0 and WinForms only works around it
    /// while the column is being inserted, so the designer - which can only set
    /// TextAlign after the fact - cannot express a right-aligned "#".
    /// </summary>
    private void RightAlignFirstColumn()
    {
        var first = pullList.Columns[0];
        if (first.TextAlign == HorizontalAlignment.Right) return;
        var (text, width) = (first.Text, first.Width);
        pullList.Columns.RemoveAt(0);
        pullList.Columns.Insert(0, text, width, HorizontalAlignment.Right);
    }

    /// <summary>Put <c>content</c>'s children back into the designer's stacking order.</summary>
    private void RestoreContentOrder()
    {
        for (var i = 0; i < _contentOrder.Length; i++)
            content.Controls.SetChildIndex(_contentOrder[i], i);
    }

    private IEnumerable<CardPanel> Cards()
    {
        yield return headerCard;
        yield return timelineCard;
        yield return pullsCard;
        yield return playersCard;
        yield return exportCard;
    }

    /// <summary>
    /// Take the designer's column widths up to the real DPI; the chapter column then
    /// absorbs whatever is left so the table never scrolls sideways.
    /// </summary>
    private void SizeColumns()
    {
        if (pullList.Columns.Count < _baseColumnWidths.Length) return;
        var s = pullList.DeviceDpi / 96.0;
        var used = 0;
        for (var i = 0; i < _baseColumnWidths.Length; i++)
        {
            if (i == 1) continue;
            pullList.Columns[i].Width = (int)(_baseColumnWidths[i] * s);
            used += pullList.Columns[i].Width;
        }
        var spare = pullList.ClientSize.Width - used - 4;
        pullList.Columns[1].Width = Math.Clamp(spare, (int)(150 * s), (int)(300 * s));
    }

    // ---- designer-wired events --------------------------------------------

    private void OnOpenClick(object? sender, EventArgs e) => OpenDialog();

    private void OnExitClick(object? sender, EventArgs e) => Close();

    private void OnRegisterOpcodesClick(object? sender, EventArgs e) => OpenDevMenu();

    private void OnInitZoneTemplateClick(object? sender, EventArgs e) => PickInitZoneTemplate();

    private void OnAboutClick(object? sender, EventArgs e) => MessageBox.Show(this,
        "Replay Workbench - FFXIV duty recording editor and splitter.\n\n" +
        $"Opcode data: {OpcodeData.Chain.Count} patches, latest {OpcodeData.LatestPatch} " +
        $"(build {OpcodeData.LatestGameBuild}).\n" +
        "Generated from docs/*.js by tools/export_core_data.py.",
        "About", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void OnDropHintClick(object? sender, EventArgs e) => OpenDialog();

    private void OnTimelinePullSelected(object? sender, int index) => SelectPull(index);

    private void OnPullListSelectionChanged(object? sender, EventArgs e)
    {
        if (pullList.SelectedIndices.Count > 0) SelectPull(pullList.SelectedIndices[0]);
    }

    private void OnAnonymizeNamesClick(object? sender, EventArgs e)
    {
        if (_file is null) return;
        for (var i = 0; i < _file.Players.Count; i++) _file.Players[i].NewName = $"Player {i + 1}";
        RenderPlayers();
        Say($"Anonymized {_file.Players.Count} names - export to save.");
    }

    private void OnExportPullClick(object? sender, EventArgs e) => ExportPull();

    private void OnExportFullClick(object? sender, EventArgs e) => ExportFull();

    private void OnPatchPicked(object? sender, EventArgs e)
    {
        // Picking a patch by hand re-parses the file: the patch decides which
        // opcode is NpcSpawn, PlaceFieldMarker and so on, so the pull list and
        // timeline have to be rebuilt against it, not just the transpose.
        if (_suppressPatchEvent || _rawBytes is null) return;
        _patchOverride = patchPick.SelectedIndex <= 0 ? null : (string)patchPick.SelectedItem!;
        Reparse();
    }

    // ---- loading ----------------------------------------------------------

    private static string? DroppedPath(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return null;
        return files[0].EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ? files[0] : null;
    }

    private void OpenDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Open duty recording",
            Filter = "FFXIV duty recording (*.dat)|*.dat|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadRecording(dlg.FileName);
    }

    private void LoadRecording(string path)
    {
        try
        {
            _rawBytes = File.ReadAllBytes(path);
            _path = path;
            _patchOverride = null;   // a fresh file gets its patch read from its own build
            _charEdits.Clear();      // character keys only mean anything in their own file
            Reparse();
        }
        catch (Exception ex)
        {
            Say(ex.Message, error: true);
        }
    }

    private void Reparse()
    {
        if (_rawBytes is null) return;
        try
        {
            // Parse works on its own copy: the export path mutates bytes in place,
            // so the loaded original has to stay pristine for the next export.
            _file = ReplayFile.Parse((byte[])_rawBytes.Clone(), Path.GetFileName(_path), _patchOverride);
        }
        catch (Exception ex)
        {
            Say(ex.Message, error: true);
            return;
        }

        _characters = CharacterEditor.Read(_file);
        // A patch change re-resolves PlayerSpawn, so a character the old patch found
        // may not exist under the new one; drop edits that no longer match anyone.
        foreach (var gone in _charEdits.Keys.Where(cid => _characters.All(c => c.CharacterKey != cid)).ToList())
            _charEdits.Remove(gone);

        dropHint.Text = $"{Path.GetFileName(_path)} - drop another recording to replace it";
        foreach (var c in Cards()) c.Visible = true;
        RestoreContentOrder();

        headerCard.Meta = Path.GetFileName(_path);
        readout.SetCells(_file.HeaderReadout());
        RenderTimeline();
        RenderPullTable();
        RenderPlayers();
        RenderOptionAvailability();

        _selectedPull = -1;
        timeline.Selected = -1;
        btnExportPull.Enabled = false;
        exportCard.Meta = "Single pull → .dat";
        exportHint.Text = "Select a pull from the timeline or table to enable export.";
        Say($"Loaded {_file.Pulls.Count} pulls, {_file.Players.Count} players from {Path.GetFileName(_path)}.");
        // Without this the same file just looks like it has nobody in it.
        if (_file.UnknownSpawnLength is { } len)
            Say($"This recording's PlayerSpawn packets are {len} bytes, a layout this build " +
                $"doesn't know (it reads {string.Join(" and ", CharacterLayout.KnownSpawnLengths)}). " +
                "Appearance editing and anonymize can't find anyone in it - names in the player " +
                "list came from a byte scan and renaming them still works.", error: true);
    }

    // ---- rendering --------------------------------------------------------

    private void RenderTimeline()
    {
        if (_file is null) return;
        var total = _file.TotalMs == 0 ? 1u : _file.TotalMs;
        var bands = new List<TimelineControl.Band>();
        for (var i = 0; i < _file.Pulls.Count; i++)
        {
            var startMs = _file.Pulls[i].Chapter.Ms;
            var endMs = i < _file.Pulls.Count - 1 ? _file.Pulls[i + 1].Chapter.Ms : total;
            bands.Add(new TimelineControl.Band(i, _file.Pulls[i].Number, startMs, endMs));
        }

        var waymarks = _file.Segments
            .Where(s => s.Opcode == _file.WaymarkOpcode ||
                        (s.Opcode == _file.WaymarkPresetOpcode && !_file.IsEmptyPreset(s)))
            .Select(s => s.Ms)
            .ToList();

        timeline.SetData(bands, waymarks, total);
        timelineCard.Meta = $"{_file.Pulls.Count} pulls · {Display.Clock(total)}";
    }

    private void RenderPullTable()
    {
        if (_file is null) return;
        pullList.BeginUpdate();
        pullList.Items.Clear();
        foreach (var p in _file.Pulls)
        {
            var name = p.Chapter.TypeName;
            if (p.Countdown is not null)
                name += $"  ⏱ {Display.Clock(p.Countdown.Ms - p.Chapter.Ms)}";
            var it = new ListViewItem(p.Number.ToString());
            it.SubItems.Add(name);
            it.SubItems.Add(Display.Clock(p.Chapter.Ms));
            it.SubItems.Add(Display.Clock(p.LengthMs)).Tag = "dim";
            it.SubItems.Add(p.CombatMs > 0 ? Display.Clock(p.CombatMs) : "-");
            it.SubItems.Add($"{p.BatchCount} spawns").Tag = "dim";
            pullList.Items.Add(it);
        }
        pullList.EndUpdate();
        // Fit the table to the pulls it has, up to a scrolling cap.
        pullsCard.SetBodyHeight(RowHeight + 16 + Math.Clamp(_file.Pulls.Count, 1, 12) * RowHeight);
        pullsCard.Meta = "none selected";
    }

    private void RenderPlayers()
    {
        if (_file is null) return;
        playerList.SuspendLayout();
        playerList.Controls.Clear();
        var recorder = _file.RecorderIndex;
        // A 32-byte name field is at most 31 characters; size the editor for that.
        var nameW = TextRenderer.MeasureText(new string('M', 26), Theme.Mono).Width;
        var gutter = TextRenderer.MeasureText("88", Theme.MonoSmall).Width + 4;
        for (var i = 0; i < _file.Players.Count; i++)
        {
            var p = _file.Players[i];
            var isRec = i == recorder;
            var row = new Panel
            {
                Width = gutter + nameW + 110, Height = RowHeight - 2, BackColor = Theme.Panel,
                Left = 0, Top = i * RowHeight,
            };
            var idx = new Label
            {
                Text = (i + 1).ToString(), Font = Theme.MonoSmall,
                ForeColor = isRec ? Theme.Amber : Theme.InkFaint,
                AutoSize = false, Width = gutter, Height = RowHeight - 8, Location = new Point(0, 2),
                TextAlign = ContentAlignment.MiddleRight,
            };
            var box = new TextBox
            {
                Text = p.Effective, MaxLength = 31, Font = Theme.Mono,
                BackColor = Theme.Panel2, ForeColor = isRec ? Theme.Amber : Theme.Ink,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(gutter + 8, 1), Width = nameW,
                Tag = p,
            };
            box.TextChanged += (s, _) =>
            {
                if (s is TextBox t && t.Tag is PlayerName pn) pn.NewName = t.Text;
            };
            row.Controls.Add(idx);
            row.Controls.Add(box);

            // Matched on the character key, never the name: two people can carry the
            // same one, and matching on text would point both rows at one of them.
            // A row with no key came from the name scan alone - someone the spawn
            // packets don't describe - so there is nothing to edit.
            var record = p.CharacterKey == 0
                ? null
                : _characters.FirstOrDefault(c => c.CharacterKey == p.CharacterKey);
            var cog = new CogButton
            {
                Location = new Point(gutter + nameW + 14, 0),
                Height = RowHeight - 4,
                Enabled = record is not null,
            };
            cog.Width = cog.Height;
            rowTips.SetToolTip(cog, record is null
                ? "No PlayerSpawn packet for this name - nothing to edit."
                : $"Edit {record.Name}: race, gender, hair, colors, gear and dyes");
            if (record is not null)
            {
                cog.Edited = _charEdits.ContainsKey(record.CharacterKey);
                cog.Click += (_, _) => EditCharacter(record, cog);
            }
            row.Controls.Add(cog);

            if (isRec)
                row.Controls.Add(new Label
                {
                    Text = "REC", Font = Theme.MonoSmall, ForeColor = Theme.Amber,
                    AutoSize = true, Location = new Point(gutter + nameW + 20 + cog.Width, 5),
                });
            playerList.Controls.Add(row);
        }
        playerList.ResumeLayout();
        // Show the whole party without scrolling where it fits; cap it past that.
        playersCard.SetBodyHeight(60 + Math.Clamp(_file.Players.Count, 1, 9) * RowHeight);
        playersCard.Meta = _charEdits.Count > 0
            ? $"{_file.Players.Count} found · {_charEdits.Count} edited"
            : $"{_file.Players.Count} found";
        btnAnonNames.Enabled = _file.Players.Count > 0;
    }

    /// <summary>Pair each pending look with the character it belongs to, dropping any
    /// whose character the current patch no longer resolves.</summary>
    private List<CharacterEdit> PendingCharacterEdits() =>
        _charEdits
            .Select(kv => (Record: _characters.FirstOrDefault(c => c.CharacterKey == kv.Key), Desired: kv.Value))
            .Where(x => x.Record is not null)
            .Select(x => new CharacterEdit { Record = x.Record!, Desired = x.Desired })
            .ToList();

    private void EditCharacter(CharacterRecord record, CogButton cog)
    {
        var current = _charEdits.TryGetValue(record.CharacterKey, out var pending) ? pending : record.Original;
        using var dlg = new CharacterForm(record, current);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (dlg.Result.SameAs(record.Original))
        {
            _charEdits.Remove(record.CharacterKey);
            Say($"{record.Name}: back to the recording's own appearance.");
        }
        else
        {
            _charEdits[record.CharacterKey] = dlg.Result;
            var c = dlg.Result.Customize;
            Say($"{record.Name}: {Customize.GenderName(c.Gender)} {Customize.ClanName(c.Clan)} " +
                $"{Customize.RaceName(c.Race)} - applied on export.");
        }
        cog.Edited = _charEdits.ContainsKey(record.CharacterKey);
        playersCard.Meta = _charEdits.Count > 0
            ? $"{_file!.Players.Count} found · {_charEdits.Count} edited"
            : $"{_file!.Players.Count} found";
    }

    /// <summary>
    /// Which export options this file can actually support, and what the patch
    /// picker should say.  The patch is read out of the file's own opcodes, with
    /// the build number as a fallback and the picker as the last word.
    /// </summary>
    private void RenderOptionAvailability()
    {
        if (_file is null) return;

        // waymarks
        var hasWm = _file.HasWaymarks();
        optWaymarks.SetAvailable(hasWm);
        optWaymarks.SubText = hasWm ? "Carry the last waymarks into the pull" : "None captured in this file";
        if (hasWm) optWaymarks.Checked = true;

        // patch picker
        _suppressPatchEvent = true;
        patchPick.Items.Clear();
        patchPick.Items.Add("unknown");
        for (var i = OpcodeData.Chain.Count - 1; i >= 0; i--) patchPick.Items.Add(OpcodeData.Chain[i]);
        // A registered table isn't in the chain but is a legitimate answer.
        if (_file.FilePatch is not null && !OpcodeData.InChain(_file.FilePatch) &&
            !patchPick.Items.Contains(_file.FilePatch))
            patchPick.Items.Insert(1, _file.FilePatch);
        patchPick.SelectedIndex = _file.FilePatch is null ? 0 : Math.Max(0, patchPick.Items.IndexOf(_file.FilePatch));
        patchPick.Enabled = true;
        _suppressPatchEvent = false;

        var det = _file.PatchDetected;
        var fromBuild = OpcodeData.BuildToPatch.GetValueOrDefault(_file.FileBuild);
        var source = _patchOverride is not null ? "you picked it"
            : det is { Confident: true } ? $"read from the file's opcodes ({det.Packets * 100:0}% fit, next best {det.RunnerUp})"
            : fromBuild is not null ? $"from build {_file.FileBuild}"
            : "not identified";
        Say($"Patch: {_file.FilePatch ?? "unknown"} ({source}).");

        // transpose
        if (_file.FilePatch is null)
        {
            optTranspose.SetAvailable(false);
            optTranspose.SubText = det is not null
                ? $"Couldn't identify the patch - closest is {det.Patch} at {det.Packets * 100:0}%; pick one"
                : $"Build {_file.FileBuild} isn't a patch we know - pick the patch it was recorded on";
        }
        else if (_file.FilePatch == OpcodeData.LatestPatch)
        {
            optTranspose.SetAvailable(false);
            optTranspose.SubText = $"Already on the latest patch ({OpcodeData.LatestPatch})";
        }
        else
        {
            var plan = Transpose.Plan(_file.FilePatch, OpcodeData.LatestPatch);
            if (!plan.Ok)
            {
                optTranspose.SetAvailable(false);
                optTranspose.SubText = $"Can't remap {_file.FilePatch}: {plan.Reason}";
            }
            else
            {
                optTranspose.SetAvailable(true);
                optTranspose.Checked = true;
                var hops = OpcodeData.InChain(_file.FilePatch) && OpcodeData.InChain(OpcodeData.LatestPatch)
                    ? OpcodeData.ChainPos(OpcodeData.LatestPatch) - OpcodeData.ChainPos(_file.FilePatch)
                    : 0;
                var msg = plan.Via == "diffs"
                    ? $"Remap {_file.FilePatch} to {OpcodeData.LatestPatch} through {hops} patch{(hops == 1 ? "" : "es")} of opcode diffs"
                    : $"Remap {_file.FilePatch} to {OpcodeData.LatestPatch} by IPC name (no diff for {OpcodeData.LatestPatch} yet)";
                // Say it out loud when the build table would have sent this file down
                // the wrong chain: a one-hotfix-off guess remaps every packet onto the
                // wrong packet type.
                if (_patchOverride is null && det is { Confident: true } && fromBuild is not null && fromBuild != det.Patch)
                    msg += $" - build {_file.FileBuild} is listed as {fromBuild}, but the packets say {det.Patch}";
                // Renumbering alone isn't enough on a recording old enough that the
                // structs have since grown, and the gap is silent: the export looks
                // fine and the client refuses it. Say so on the option itself.
                var old = PayloadMigrator.OldSized(_file);
                if (old.Count > 0)
                {
                    msg += $" · also resizing {old.Count} packet type{(old.Count == 1 ? "" : "s")} " +
                           $"({string.Join(", ", old.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}→{PayloadMigrator.TargetSize[kv.Key]}"))})";
                    if (old.ContainsKey("InitZone"))
                        msg += _initZoneTemplate is null
                            ? " · InitZone needs a template: Tools ▸ Set InitZone template… (a recent recording, same duty is best) or it will not load"
                            : $" · InitZone rebuilt from {_initZoneTemplateFrom}";
                }
                optTranspose.SubText = msg;
            }
        }

        // anonymize works on any loaded file with players; off by default
        optAnon.SetAvailable(_file.Players.Count > 0);
        SyncRaceEnabled();

        // strip portraits: needs the file's opcode table to find PartyPortraitInfo
        var stripOk = PortraitStripper.IsAvailable(_file.FilePatch);
        optStrip.SetAvailable(stripOk);
        optStrip.SubText = stripOk
            ? "Delete the PartyPortraitInfo packets entirely"
            : "No PartyPortraitInfo entry for this patch";
    }

    /// <summary>One player row, sized off the editor font rather than a fixed pixel count.</summary>
    private static int RowHeight => Theme.Mono.Height + 14;

    private void SyncRaceEnabled() => racePick.Enabled = optAnon.Box.Enabled && optAnon.Checked;

    private void SelectPull(int idx)
    {
        if (_file is null || idx < 0 || idx >= _file.Pulls.Count) return;
        _selectedPull = idx;
        timeline.Selected = idx;
        if (pullList.SelectedIndices.Count == 0 || pullList.SelectedIndices[0] != idx)
        {
            pullList.SelectedIndices.Clear();
            pullList.Items[idx].Selected = true;
            pullList.EnsureVisible(idx);
        }
        var p = _file.Pulls[idx];
        pullsCard.Meta = $"pull {p.Number} · {p.Chapter.TypeName} · {Display.Clock(p.Chapter.Ms)}";
        btnExportPull.Enabled = true;
        exportHint.Text =
            $"Pull {p.Number} ready to export (opens at {Display.Clock(p.Chapter.Ms)}, {p.BatchCount} actors respawned).";
    }

    // ---- export -----------------------------------------------------------

    private ExportOptions CurrentOptions() => new()
    {
        Waymarks = optWaymarks.Box.Enabled && optWaymarks.Checked,
        ApplyNames = optNames.Checked,
        Countdown = optCountdown.Checked,
    };

    /// <summary>
    /// The post-build passes, in the order the format requires: anonymize and
    /// strip while packets still carry the file's own opcodes, transpose last.
    /// </summary>
    private (byte[] Bytes, string Note) ApplyPostPasses(byte[] bytes)
    {
        var note = "";
        var anon = AnonymizeResult.None;
        if (optAnon.Box.Enabled && optAnon.Checked)
        {
            anon = Anonymizer.Apply(bytes, _file!.FilePatch, Customize.Races[Math.Max(0, racePick.SelectedIndex)].Id);
            note += anon.Note;
        }
        // After the blanket anonymize, so a deliberate per-character look wins over
        // it — following the key remap, since anonymizing moved the people.
        note += CharacterEditor.Apply(bytes, _file!.FilePatch, PendingCharacterEdits(), anon.KeyRemap);
        if (optStrip.Box.Enabled && optStrip.Checked)
        {
            var s = PortraitStripper.Strip(bytes, _file!.FilePatch);
            bytes = s.Bytes;
            note += s.Note;
        }
        if (optTranspose.Box.Enabled && optTranspose.Checked)
        {
            // Resize before renumbering: packets are picked by name in the file's
            // own patch, and the two are useless apart - a file with new opcodes
            // and old payload sizes is exactly what the client refuses to read.
            var m = PayloadMigrator.Apply(bytes, _file!.FilePatch, _initZoneTemplate);
            bytes = m.Bytes;
            note += m.Note;
            note += Transpose.ApplyAndStamp(bytes, _file!.FilePatch, _file.FileBuild);
        }
        return (bytes, note);
    }

    private void ExportPull()
    {
        if (_file is null || _selectedPull < 0) return;
        try
        {
            var ex = PullExporter.BuildPull(_file, _selectedPull, CurrentOptions());
            var (bytes, note) = ApplyPostPasses(ex.Bytes);
            var ghosts = ex.GhostsDropped > 0
                ? $" · removed {ex.GhostsDropped} stale duplicate spawn{(ex.GhostsDropped > 1 ? "s" : "")}"
                : "";
            var baseName = Path.GetFileNameWithoutExtension(_path);
            var suggested = $"pull{_file.Pulls[_selectedPull].Number}_{baseName}.dat";
            if (!Save(bytes, suggested)) return;
            Say($"Exported pull {_file.Pulls[_selectedPull].Number} ({Display.Bytes(bytes.Length)}){note}{ghosts}.");
        }
        catch (Exception e)
        {
            Say(e.Message, error: true);
        }
    }

    private void ExportFull()
    {
        if (_file is null) return;
        try
        {
            var bytes = PullExporter.BuildRenamedFull(_file);
            var (final, note) = ApplyPostPasses(bytes);
            if (!Save(final, $"RENAMED_{Path.GetFileName(_path)}")) return;
            Say($"Exported full recording with edited names ({Display.Bytes(final.Length)}){note}.");
        }
        catch (Exception e)
        {
            Say(e.Message, error: true);
        }
    }

    private bool Save(byte[] bytes, string suggestedName)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save export",
            FileName = suggestedName,
            Filter = "FFXIV duty recording (*.dat)|*.dat|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_path) ?? "",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        File.WriteAllBytes(dlg.FileName, bytes);
        return true;
    }

    /// <summary>
    /// Pick a recording to lift a working InitZone out of.  Old recordings need
    /// one because InitZone is the single packet whose change can't be expressed
    /// as a resize - it drops bytes as well as adding them, so it is rebuilt from
    /// a payload the live client already accepted.
    /// </summary>
    private void PickInitZoneTemplate()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Recording to take a current InitZone from (same duty is best)",
            Filter = "FFXIV duty recording (*.dat)|*.dat|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_path) ?? "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var name = Path.GetFileName(dlg.FileName);
            var payload = PayloadMigrator.ReadInitZoneTemplate(
                File.ReadAllBytes(dlg.FileName), name, out var error);
            if (payload is null) { Say(error ?? "couldn't read an InitZone from that file", error: true); return; }
            _initZoneTemplate = payload;
            _initZoneTemplateFrom = name;
            Say($"InitZone template set from {name} ({payload.Length} bytes).");
            if (_file is not null) RenderOptionAvailability();
        }
        catch (Exception e)
        {
            Say(e.Message, error: true);
        }
    }

    // ---- dev menu ---------------------------------------------------------

    private void OpenDevMenu()
    {
        using var dlg = new DevMenuForm();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Say($"Registered {dlg.RegisteredCount} opcodes for build {dlg.RegisteredBuild} (now latest).");
        if (_rawBytes is not null) Reparse();
    }

    // ---- log --------------------------------------------------------------

    private void Say(string message, bool error = false)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        logBox.AppendText($"{stamp}  {(error ? "! " : "")}{message}{Environment.NewLine}");
    }
}
