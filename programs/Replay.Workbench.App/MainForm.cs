using ReplayWorkbench.Core;

namespace ReplayWorkbench.App;

/// <summary>
/// The editor: load a duty recording, read its header, pick a pull off the
/// timeline, and export that pull as a standalone .dat.  A desktop port of the
/// Editor tab of docs/index.html; all of the actual work lives in
/// Replay.Workbench.Core.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly Panel _content;
    private readonly TextBox _log;
    private readonly Label _dropHint;

    private readonly CardPanel _headerCard;
    private readonly CardPanel _timelineCard;
    private readonly CardPanel _pullsCard;
    private readonly CardPanel _playersCard;
    private readonly CardPanel _exportCard;

    private readonly ReadoutView _readout = new();
    private readonly TimelineControl _timeline = new();
    private readonly DarkListView _pullList = new();
    private readonly Panel _playerList = new() { AutoScroll = true, BackColor = Theme.Panel, Height = 120 };

    private readonly OptionCheck _optWaymarks = new("Carry waymarks", "Carry the last waymarks into the pull");
    private readonly OptionCheck _optNames = new("Apply name edits", "Write the names typed above into the export");
    private readonly OptionCheck _optCountdown = new("Keep engage chapter", "Expose the countdown/engage as a second chapter");
    private readonly OptionCheck _optTranspose = new("Transpose opcodes", "Remap packets so the current client reads them");
    private readonly OptionCheck _optAnon = new("Anonymize players", "Replace names, object IDs, character keys, race, gear & weapons so no one is identifiable");
    private readonly OptionCheck _optStrip = new("Strip party portraits", "Delete the PartyPortraitInfo packets entirely");

    private readonly ComboBox _patchPick = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _racePick = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    /// <summary>An InitZone payload lifted from a current-layout recording, for
    /// rebuilding an old one. Set from Tools; kept for the life of the process.</summary>
    private byte[]? _initZoneTemplate;
    private string? _initZoneTemplateFrom;
    private readonly FlatButton _btnExportPull = new("Export selected pull") { Accent = true, Width = 190 };
    private readonly FlatButton _btnExportFull = new("Export renamed full file") { Width = 190 };
    private readonly FlatButton _btnAnonNames = new("Anonymize all names") { Width = 160 };
    private readonly Label _exportHint = new();

    private Panel? _pullsBody;
    private Panel? _playersBody;

    /// <summary>Characters with a PlayerSpawn in the loaded file.</summary>
    private IReadOnlyList<CharacterRecord> _characters = Array.Empty<CharacterRecord>();

    /// <summary>Pending per-character looks, keyed by the character key so they survive a
    /// re-parse (picking a patch by hand rebuilds the file but not the people).</summary>
    private readonly Dictionary<ulong, CharacterAppearance> _charEdits = new();

    private ReplayFile? _file;
    private byte[]? _rawBytes;
    private string _path = "";
    /// <summary>The patch the user picked by hand, if any - survives a re-parse.</summary>
    private string? _patchOverride;
    private int _selectedPull = -1;
    private bool _suppressPatchEvent;

    public MainForm(string? openOnStart = null)
    {
        Text = "Replay Workbench";
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        Font = Theme.Sans;
        MinimumSize = new Size(880, 640);
        ClientSize = new Size(1060, 900);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        KeyPreview = true;

        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.Panel,
            ForeColor = Theme.InkDim,
            Font = Theme.MonoSmall,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            TabStop = false,
        };
        var logHost = new Panel { Dock = DockStyle.Bottom, Height = 96, BackColor = Theme.Panel, Padding = new Padding(12, 8, 12, 8) };
        logHost.Controls.Add(_log);
        logHost.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Line);
            e.Graphics.DrawLine(p, 0, 0, logHost.Width, 0);
        };

        _content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(20, 12, 20, 20) };

        _dropHint = new Label
        {
            Text = "Drop a duty recording here, or use File ▸ Open…",
            Font = Theme.Mono,
            ForeColor = Theme.InkDim,
            BackColor = Theme.Bg,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = 92,
        };
        _dropHint.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Line) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            e.Graphics.DrawRectangle(p, 0, 0, _dropHint.Width - 1, _dropHint.Height - 1);
        };
        _dropHint.Click += (_, _) => OpenDialog();
        _dropHint.Cursor = Cursors.Hand;

        _headerCard = new CardPanel("Recording Header") { Content = _readout };
        _timelineCard = new CardPanel("Pull Timeline") { Content = _timeline };
        _pullsCard = new CardPanel("Pulls") { Content = BuildPullList() };
        _playersCard = new CardPanel("Players") { Content = BuildPlayersBody() };
        _exportCard = new CardPanel("Export") { Content = BuildExportBody(), Meta = "Single pull → .dat" };

        foreach (var c in Cards()) { c.Visible = false; c.HeightChanged += (_, _) => Relayout(); }

        _content.Controls.Add(_dropHint);
        foreach (var c in Cards()) _content.Controls.Add(c);
        _content.Resize += (_, _) => Relayout();

        Controls.Add(_content);
        Controls.Add(logHost);
        Controls.Add(BuildMenu());

        DragEnter += (_, e) => e.Effect = DroppedPath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { var p = DroppedPath(e); if (p is not null) LoadRecording(p); };

        _timeline.PullSelected += (_, idx) => SelectPull(idx);
        _pullList.SelectedIndexChanged += (_, _) =>
        {
            if (_pullList.SelectedIndices.Count > 0) SelectPull(_pullList.SelectedIndices[0]);
        };

        Relayout();
        Say("Ready. Drop a .dat on the window to begin.");

        // Wait for the window to exist: loading relayouts against real client sizes.
        if (openOnStart is not null)
            Shown += (_, _) => LoadRecording(openOnStart);
    }

    private IEnumerable<CardPanel> Cards()
    {
        yield return _headerCard;
        yield return _timelineCard;
        yield return _pullsCard;
        yield return _playersCard;
        yield return _exportCard;
    }

    // ---- chrome -----------------------------------------------------------

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip
        {
            BackColor = Theme.Panel2,
            ForeColor = Theme.Ink,
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()),
            Padding = new Padding(6, 2, 0, 2),
        };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&Open recording…", Keys.Control | Keys.O, (_, _) => OpenDialog()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("E&xit", Keys.Alt | Keys.F4, (_, _) => Close()));

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add(Item("&Register opcode table…", Keys.None, (_, _) => OpenDevMenu()));
        tools.DropDownItems.Add(Item("Set &InitZone template…", Keys.None, (_, _) => PickInitZoneTemplate()));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("&About", Keys.None, (_, _) => MessageBox.Show(this,
            "Replay Workbench - FFXIV duty recording editor and splitter.\n\n" +
            $"Opcode data: {OpcodeData.Chain.Count} patches, latest {OpcodeData.LatestPatch} " +
            $"(build {OpcodeData.LatestGameBuild}).\n" +
            "Generated from docs/*.js by tools/export_core_data.py.",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        menu.Items.AddRange(new ToolStripItem[] { file, tools, help });
        foreach (ToolStripMenuItem top in menu.Items) Paint(top);
        return menu;

        static ToolStripMenuItem Item(string text, Keys keys, EventHandler onClick)
        {
            var it = new ToolStripMenuItem(text) { ShortcutKeys = keys };
            it.Click += onClick;
            return it;
        }

        static void Paint(ToolStripMenuItem item)
        {
            item.BackColor = Theme.Panel2;
            item.ForeColor = Theme.Ink;
            foreach (var child in item.DropDownItems)
                if (child is ToolStripMenuItem m) Paint(m);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.Line;
        public override Color MenuItemSelectedGradientBegin => Theme.Line;
        public override Color MenuItemSelectedGradientEnd => Theme.Line;
        public override Color MenuItemBorder => Theme.PhosphorDeep;
        public override Color MenuBorder => Theme.Line;
        public override Color ToolStripDropDownBackground => Theme.Panel2;
        public override Color ImageMarginGradientBegin => Theme.Panel2;
        public override Color ImageMarginGradientMiddle => Theme.Panel2;
        public override Color ImageMarginGradientEnd => Theme.Panel2;
        public override Color MenuStripGradientBegin => Theme.Panel2;
        public override Color MenuStripGradientEnd => Theme.Panel2;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Line;
    }

    private Control BuildPullList()
    {
        // Column widths are raw pixels, so they take the DPI scale by hand; the
        // chapter column then absorbs whatever is left so nothing scrolls sideways.
        void SizeColumns()
        {
            if (_pullList.Columns.Count < 6) return;
            var s = _pullList.DeviceDpi / 96.0;
            int[] fixedW = { 44, 0, 112, 112, 112, 124 };
            var used = 0;
            for (var i = 0; i < fixedW.Length; i++)
            {
                if (i == 1) continue;
                _pullList.Columns[i].Width = (int)(fixedW[i] * s);
                used += _pullList.Columns[i].Width;
            }
            var spare = _pullList.ClientSize.Width - used - 4;
            _pullList.Columns[1].Width = Math.Clamp(spare, (int)(150 * s), (int)(300 * s));
        }
        _pullList.HandleCreated += (_, _) => SizeColumns();
        _pullList.Resize += (_, _) => SizeColumns();
        _pullList.Columns.Add("#", 44, HorizontalAlignment.Right);
        _pullList.Columns.Add("chapter", 240);
        _pullList.Columns.Add("at", 112);
        _pullList.Columns.Add("length", 112);
        _pullList.Columns.Add("combat", 112, HorizontalAlignment.Right);
        _pullList.Columns.Add("respawn batch", 124, HorizontalAlignment.Right);
        var host = new Panel { BackColor = Theme.Panel, Padding = new Padding(1, 0, 1, 8) };
        host.Height = RowHeight * 8 + 40;
        _pullsBody = host;
        _pullList.Dock = DockStyle.Fill;
        host.Controls.Add(_pullList);
        return host;
    }

    private Control BuildPlayersBody()
    {
        var host = new Panel { Height = 300, BackColor = Theme.Panel, Padding = new Padding(14, 10, 14, 12) };
        var buttonRow = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.Panel };
        _btnAnonNames.Location = new Point(0, 0);
        _btnAnonNames.Click += (_, _) =>
        {
            if (_file is null) return;
            for (var i = 0; i < _file.Players.Count; i++) _file.Players[i].NewName = $"Player {i + 1}";
            RenderPlayers();
            Say($"Anonymized {_file.Players.Count} names - export to save.");
        };
        buttonRow.Controls.Add(_btnAnonNames);
        _playerList.Dock = DockStyle.Fill;
        host.Controls.Add(_playerList);
        host.Controls.Add(buttonRow);
        _playersBody = host;
        return host;
    }

    private Control BuildExportBody()
    {
        var host = new Panel { BackColor = Theme.Panel, Padding = new Padding(14, 12, 14, 14) };

        var rowH = _optAnon.Height + 8;
        var opts = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 4,
            Height = rowH * 4,
            BackColor = Theme.Panel,
        };
        opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < 4; i++) opts.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));

        foreach (var o in new[] { _optWaymarks, _optNames, _optCountdown, _optTranspose, _optAnon, _optStrip })
        {
            o.Dock = DockStyle.Top;
            o.Margin = new Padding(0, 0, 24, 0);
        }

        opts.Controls.Add(_optWaymarks, 0, 0);
        opts.Controls.Add(_optNames, 1, 0);
        opts.Controls.Add(_optCountdown, 0, 1);
        opts.Controls.Add(_optTranspose, 1, 1);
        opts.Controls.Add(_optAnon, 0, 2);
        opts.Controls.Add(_optStrip, 1, 2);
        opts.Controls.Add(PatchRow(), 0, 3);
        opts.Controls.Add(RaceRow(), 1, 3);

        _optWaymarks.Checked = true;
        _optNames.Checked = true;
        _optCountdown.Checked = true;
        _optAnon.Box.CheckedChanged += (_, _) => SyncRaceEnabled();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = _btnExportPull.Height + 14,
            BackColor = Theme.Panel,
            Padding = new Padding(0, 8, 0, 0),
        };
        _btnExportPull.Click += (_, _) => ExportPull();
        _btnExportFull.Click += (_, _) => ExportFull();
        buttons.Controls.Add(_btnExportPull);
        buttons.Controls.Add(_btnExportFull);

        _exportHint.Dock = DockStyle.Top;
        _exportHint.Height = Theme.MonoSmall.Height + 8;
        _exportHint.Font = Theme.MonoSmall;
        _exportHint.ForeColor = Theme.InkDim;
        _exportHint.Text = "Select a pull from the timeline or table to enable export.";

        host.Controls.Add(_exportHint);
        host.Controls.Add(buttons);
        host.Controls.Add(opts);
        host.Height = opts.Height + buttons.Height + _exportHint.Height + host.Padding.Vertical;
        _btnExportPull.Enabled = false;
        return host;
    }

    private Control PatchRow()
    {
        var row = FieldRow("Recorded on", _patchPick);
        _patchPick.Width = 150;
        _patchPick.BackColor = Theme.Panel2;
        _patchPick.ForeColor = Theme.Ink;
        _patchPick.FlatStyle = FlatStyle.Flat;
        _patchPick.Font = Theme.Mono;
        _patchPick.Enabled = false;
        _patchPick.SelectedIndexChanged += (_, _) =>
        {
            // Picking a patch by hand re-parses the file: the patch decides which
            // opcode is NpcSpawn, PlaceFieldMarker and so on, so the pull list and
            // timeline have to be rebuilt against it, not just the transpose.
            if (_suppressPatchEvent || _rawBytes is null) return;
            _patchOverride = _patchPick.SelectedIndex <= 0 ? null : (string)_patchPick.SelectedItem!;
            Reparse();
        };
        return row;
    }

    /// <summary>A caption followed by a field, flowed so the field clears the caption.</summary>
    private static FlowLayoutPanel FieldRow(string caption, Control field)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = false,
            BackColor = Theme.Panel, Padding = new Padding(0, 3, 0, 0),
        };
        row.Controls.Add(new Label
        {
            Text = caption, Font = Theme.Sans, ForeColor = Theme.Ink,
            AutoSize = true, Margin = new Padding(0, 5, 10, 0),
        });
        field.Margin = new Padding(0);
        row.Controls.Add(field);
        return row;
    }

    private Control RaceRow()
    {
        var row = FieldRow("Race", _racePick);
        _racePick.Width = 150;
        _racePick.BackColor = Theme.Panel2;
        _racePick.ForeColor = Theme.Ink;
        _racePick.FlatStyle = FlatStyle.Flat;
        _racePick.Font = Theme.Mono;
        foreach (var (_, name) in Customize.Races) _racePick.Items.Add(name);
        _racePick.SelectedIndex = 0;
        _racePick.Enabled = false;
        return row;
    }

    // ---- layout -----------------------------------------------------------

    private void Relayout()
    {
        var x = _content.Padding.Left;
        var w = _content.ClientSize.Width - _content.Padding.Left - _content.Padding.Right;
        if (w < 200) return;
        var y = _content.Padding.Top;

        _dropHint.SetBounds(x, y, w, _dropHint.Height);
        y += _dropHint.Height + 16;

        foreach (var card in Cards())
        {
            if (!card.Visible) continue;
            card.SetBounds(x, y, w, card.Height);
            y += card.Height + 14;
        }
        // Cards resize as content changes; the background they vacate is ours to repaint.
        _content.Invalidate();
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

        _dropHint.Text = $"{Path.GetFileName(_path)} - drop another recording to replace it";
        foreach (var c in Cards()) c.Visible = true;

        _headerCard.Meta = Path.GetFileName(_path);
        _readout.SetCells(_file.HeaderReadout());
        RenderTimeline();
        RenderPullTable();
        RenderPlayers();
        RenderOptionAvailability();

        _selectedPull = -1;
        _timeline.Selected = -1;
        _btnExportPull.Enabled = false;
        _exportCard.Meta = "Single pull → .dat";
        _exportHint.Text = "Select a pull from the timeline or table to enable export.";
        Relayout();
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

        _timeline.SetData(bands, waymarks, total);
        _timelineCard.Meta = $"{_file.Pulls.Count} pulls · {Display.Clock(total)}";
    }

    private void RenderPullTable()
    {
        if (_file is null) return;
        _pullList.BeginUpdate();
        _pullList.Items.Clear();
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
            _pullList.Items.Add(it);
        }
        _pullList.EndUpdate();
        // Fit the table to the pulls it has, up to a scrolling cap.
        if (_pullsBody is not null)
            _pullsBody.Height = RowHeight + 16 + Math.Clamp(_file.Pulls.Count, 1, 12) * RowHeight;
        _pullsCard.Meta = "none selected";
    }

    private void RenderPlayers()
    {
        if (_file is null) return;
        _playerList.SuspendLayout();
        _playerList.Controls.Clear();
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
            _rowTips.SetToolTip(cog, record is null
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
            _playerList.Controls.Add(row);
        }
        _playerList.ResumeLayout();
        // Show the whole party without scrolling where it fits; cap it past that.
        if (_playersBody is not null)
            _playersBody.Height = 60 + Math.Clamp(_file.Players.Count, 1, 9) * RowHeight;
        _playersCard.Meta = _charEdits.Count > 0
            ? $"{_file.Players.Count} found · {_charEdits.Count} edited"
            : $"{_file.Players.Count} found";
        _btnAnonNames.Enabled = _file.Players.Count > 0;
    }

    /// <summary>Pair each pending look with the character it belongs to, dropping any
    /// whose character the current patch no longer resolves.</summary>
    private List<CharacterEdit> PendingCharacterEdits() =>
        _charEdits
            .Select(kv => (Record: _characters.FirstOrDefault(c => c.CharacterKey == kv.Key), Desired: kv.Value))
            .Where(x => x.Record is not null)
            .Select(x => new CharacterEdit { Record = x.Record!, Desired = x.Desired })
            .ToList();

    private readonly ToolTip _rowTips = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 12000 };

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
        _playersCard.Meta = _charEdits.Count > 0
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
        _optWaymarks.SetAvailable(hasWm);
        _optWaymarks.SubText = hasWm ? "Carry the last waymarks into the pull" : "None captured in this file";
        if (hasWm) _optWaymarks.Checked = true;

        // patch picker
        _suppressPatchEvent = true;
        _patchPick.Items.Clear();
        _patchPick.Items.Add("unknown");
        for (var i = OpcodeData.Chain.Count - 1; i >= 0; i--) _patchPick.Items.Add(OpcodeData.Chain[i]);
        // A registered table isn't in the chain but is a legitimate answer.
        if (_file.FilePatch is not null && !OpcodeData.InChain(_file.FilePatch) &&
            !_patchPick.Items.Contains(_file.FilePatch))
            _patchPick.Items.Insert(1, _file.FilePatch);
        _patchPick.SelectedIndex = _file.FilePatch is null ? 0 : Math.Max(0, _patchPick.Items.IndexOf(_file.FilePatch));
        _patchPick.Enabled = true;
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
            _optTranspose.SetAvailable(false);
            _optTranspose.SubText = det is not null
                ? $"Couldn't identify the patch - closest is {det.Patch} at {det.Packets * 100:0}%; pick one"
                : $"Build {_file.FileBuild} isn't a patch we know - pick the patch it was recorded on";
        }
        else if (_file.FilePatch == OpcodeData.LatestPatch)
        {
            _optTranspose.SetAvailable(false);
            _optTranspose.SubText = $"Already on the latest patch ({OpcodeData.LatestPatch})";
        }
        else
        {
            var plan = Transpose.Plan(_file.FilePatch, OpcodeData.LatestPatch);
            if (!plan.Ok)
            {
                _optTranspose.SetAvailable(false);
                _optTranspose.SubText = $"Can't remap {_file.FilePatch}: {plan.Reason}";
            }
            else
            {
                _optTranspose.SetAvailable(true);
                _optTranspose.Checked = true;
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
                _optTranspose.SubText = msg;
            }
        }

        // anonymize works on any loaded file with players; off by default
        _optAnon.SetAvailable(_file.Players.Count > 0);
        SyncRaceEnabled();

        // strip portraits: needs the file's opcode table to find PartyPortraitInfo
        var stripOk = PortraitStripper.IsAvailable(_file.FilePatch);
        _optStrip.SetAvailable(stripOk);
        _optStrip.SubText = stripOk
            ? "Delete the PartyPortraitInfo packets entirely"
            : "No PartyPortraitInfo entry for this patch";
    }

    /// <summary>One player row, sized off the editor font rather than a fixed pixel count.</summary>
    private static int RowHeight => Theme.Mono.Height + 14;

    private void SyncRaceEnabled() => _racePick.Enabled = _optAnon.Box.Enabled && _optAnon.Checked;

    private void SelectPull(int idx)
    {
        if (_file is null || idx < 0 || idx >= _file.Pulls.Count) return;
        _selectedPull = idx;
        _timeline.Selected = idx;
        if (_pullList.SelectedIndices.Count == 0 || _pullList.SelectedIndices[0] != idx)
        {
            _pullList.SelectedIndices.Clear();
            _pullList.Items[idx].Selected = true;
            _pullList.EnsureVisible(idx);
        }
        var p = _file.Pulls[idx];
        _pullsCard.Meta = $"pull {p.Number} · {p.Chapter.TypeName} · {Display.Clock(p.Chapter.Ms)}";
        _btnExportPull.Enabled = true;
        _exportHint.Text =
            $"Pull {p.Number} ready to export (opens at {Display.Clock(p.Chapter.Ms)}, {p.BatchCount} actors respawned).";
    }

    // ---- export -----------------------------------------------------------

    private ExportOptions CurrentOptions() => new()
    {
        Waymarks = _optWaymarks.Box.Enabled && _optWaymarks.Checked,
        ApplyNames = _optNames.Checked,
        Countdown = _optCountdown.Checked,
    };

    /// <summary>
    /// The post-build passes, in the order the format requires: anonymize and
    /// strip while packets still carry the file's own opcodes, transpose last.
    /// </summary>
    private (byte[] Bytes, string Note) ApplyPostPasses(byte[] bytes)
    {
        var note = "";
        var anon = AnonymizeResult.None;
        if (_optAnon.Box.Enabled && _optAnon.Checked)
        {
            anon = Anonymizer.Apply(bytes, _file!.FilePatch, Customize.Races[Math.Max(0, _racePick.SelectedIndex)].Id);
            note += anon.Note;
        }
        // After the blanket anonymize, so a deliberate per-character look wins over
        // it — following the key remap, since anonymizing moved the people.
        note += CharacterEditor.Apply(bytes, _file!.FilePatch, PendingCharacterEdits(), anon.KeyRemap);
        if (_optStrip.Box.Enabled && _optStrip.Checked)
        {
            var s = PortraitStripper.Strip(bytes, _file!.FilePatch);
            bytes = s.Bytes;
            note += s.Note;
        }
        if (_optTranspose.Box.Enabled && _optTranspose.Checked)
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
        _log.AppendText($"{stamp}  {(error ? "! " : "")}{message}{Environment.NewLine}");
    }
}
