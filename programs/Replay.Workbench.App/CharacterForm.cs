using ReplayWorkbench.Core;

namespace ReplayWorkbench.App;

/// <summary>
/// Per-character appearance editor: the customize block, gear and dyes, weapons,
/// facewear, title, worlds, status icon and the display toggles, for one player in
/// the recording.
/// </summary>
internal sealed class CharacterForm : Form
{
    private readonly CharacterRecord _record;

    private readonly ComboBox _race = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _gender = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _clan = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown?[] _cust = new NumericUpDown?[Customize.Length];

    private readonly NumericUpDown[,] _gear = new NumericUpDown[CharacterLayout.GearSlots, 5];
    private readonly NumericUpDown[,] _weapon = new NumericUpDown[2, 4];
    private readonly NumericUpDown _facewear = new();
    private readonly NumericUpDown _title = new();
    private readonly NumericUpDown _curWorld = new();
    private readonly NumericUpDown _homeWorld = new();
    private readonly ComboBox _online = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _hideHead = new() { Text = "Hide headgear" };
    private readonly CheckBox _hideWeapon = new() { Text = "Hide weapon" };

    private Panel _headerPanel = null!;
    private Panel _appearancePanel = null!;
    private Panel _gearPanel = null!;
    private Panel _footerPanel = null!;
    private Label _appearanceNote = null!;
    private Label _gearNote = null!;
    private FlowLayoutPanel _headerButtons = null!;

    private bool _syncingRace;

    /// <summary>The edited appearance, valid once the dialog returns OK.</summary>
    public CharacterAppearance Result { get; private set; }

    public CharacterForm(CharacterRecord record, CharacterAppearance current)
    {
        _record = record;
        Result = current.Clone();

        Text = $"Character Editor: {record.Name}";
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        Font = Theme.Sans;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Font;

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(16) };

        _headerPanel = BuildHeader();
        _appearancePanel = BuildAppearance();
        _gearPanel = BuildGear();

        root.Controls.Add(_headerPanel);
        root.Controls.Add(_appearancePanel);
        root.Controls.Add(_gearPanel);
        Controls.Add(root);

        _footerPanel = BuildFooter();
        Controls.Add(_footerPanel);

        FitLayout();
        MinimumSize = new Size(640, 480);

        LoadAppearance(current);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitLayout();
    }

    /// <summary>
    /// Position the cards and size the window from what the controls actually
    /// measure. Run once while building and again once the form is shown: fonts,
    /// auto-sized labels and buttons all settle at their real DPI only after the
    /// handles exist, and sizing from stale numbers leaves dead space and clipping.
    /// </summary>
    private void FitLayout()
    {
        // header: the button row decides the height, so pin the flow panel to its
        // buttons first - its auto-size keeps slack that reads as a dead strip.
        _headerButtons.PerformLayout();
        if (_headerButtons.Controls.Count > 0)
        {
            _headerButtons.AutoSize = false;
            _headerButtons.Height = _headerButtons.Controls.Cast<Control>().Max(c => c.Bottom);
            _headerButtons.Width = _headerButtons.Controls.Cast<Control>().Max(c => c.Right);
        }
        _headerPanel.Height = _headerButtons.Bounds.Bottom + 12;

        // notes wrap, so ask how tall they really are at their width
        foreach (var (note, panel) in new[] { (_appearanceNote, _appearancePanel), (_gearNote, _gearPanel) })
        {
            var h = TextRenderer.MeasureText(note.Text, note.Font,
                new Size(note.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
            note.Height = h + 6;
            panel.Height = note.Bounds.Bottom + 12;
        }

        _headerPanel.Location = new Point(16, 16);
        _appearancePanel.Location = new Point(16, _headerPanel.Bottom + 12);
        _gearPanel.Location = new Point(_appearancePanel.Right + 14, _headerPanel.Bottom + 12);
        _headerPanel.Width = _appearancePanel.Width + 14 + _gearPanel.Width;

        var want = new Size(
            _gearPanel.Right + 16,
            Math.Max(_appearancePanel.Bottom, _gearPanel.Bottom) + 16 + _footerPanel.Height);
        // Never grow past the screen the dialog is on; the root panel scrolls.
        var screen = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min(want.Width, screen.Width - 80),
                              Math.Min(want.Height, screen.Height - 80));
    }

    // ---- chrome -----------------------------------------------------------

    private Panel BuildHeader()
    {
        var host = new Panel { BackColor = Theme.Panel, Height = 78, Padding = new Padding(14, 10, 14, 10) };
        host.Paint += (_, e) => Theme.DrawFrame(e.Graphics, host.ClientRectangle);

        var title = new Label
        {
            Text = $"{_record.Name}  ·  {_record.JobName}",
            Font = Theme.MonoBold, ForeColor = Theme.Ink, AutoSize = true, Location = new Point(14, 10),
        };
        var sub = new Label
        {
            Text = $"key 0x{_record.CharacterKey:x16}  -  {_record.SpawnPackets} spawn packet" +
                   $"{(_record.SpawnPackets == 1 ? "" : "s")}, {_record.PortraitBlocks} portrait block" +
                   $"{(_record.PortraitBlocks == 1 ? "" : "s")}",
            Font = Theme.MonoSmall, ForeColor = Theme.InkDim, AutoSize = true, Location = new Point(14, 32),
        };

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, BackColor = Theme.Panel,
            Location = new Point(12, 48),
        };
        var copy = new FlatButton("Copy look");
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(ReadCustomize().ToHex());
            Log("Customize block copied - paste it into another character.");
        };
        var paste = new FlatButton("Paste look");
        paste.Click += (_, _) =>
        {
            var c = Customize.FromHex(Clipboard.ContainsText() ? Clipboard.GetText() : "");
            if (c is null) { Log("Clipboard doesn't hold a 26-byte customize block.", true); return; }
            WriteCustomize(c);
            Log("Pasted a customize block.");
        };
        var af = new FlatButton($"Dress in {_record.JobName} artifact gear");
        af.Click += (_, _) =>
        {
            var g = OpcodeData.GearForJob(_record.Job);
            if (g is null) { Log($"No artifact gear set known for job {_record.Job}.", true); return; }
            var a = ReadAll();
            a.ApplyJobGear(g);
            LoadAppearance(a);
            Log("Applied artifact gear.");
        };
        var reset = new FlatButton("Revert to original");
        reset.Click += (_, _) => { LoadAppearance(_record.Original); Log("Reverted to the recording's own values."); };

        buttons.Controls.AddRange(new Control[] { copy, paste, af, reset });
        _headerButtons = buttons;

        host.Controls.Add(title);
        host.Controls.Add(sub);
        host.Controls.Add(buttons);
        // FlatButton re-measures itself when its handle is made, which moves the
        // flow panel's bottom; size the card from where the buttons actually end.
        buttons.Location = new Point(12, sub.Bottom + 8);
        buttons.PerformLayout();
        host.Height = buttons.Bottom + 12;
        return host;
    }

    private Panel BuildAppearance()
    {
        var host = new Panel { BackColor = Theme.Panel };
        host.Paint += (_, e) =>
        {
            Theme.DrawFrame(e.Graphics, host.ClientRectangle);
            TextRenderer.DrawText(e.Graphics, "Appearance", Theme.SansBold, new Point(14, 10),
                Theme.Ink, TextFormatFlags.NoPrefix);
        };

        // Sizes come from measured text, not fixed pixels: the fonts are in points
        // and so already scale with DPI, but hard-coded box widths do not.
        var labelW = Customize.Fields.Max(f => TextRenderer.MeasureText(f.Name, Theme.MonoSmall).Width) + 10;
        var comboW = Customize.Clans.Max(c => TextRenderer.MeasureText($"{c.Name} (00)", Theme.Mono).Width) + 34;
        var fieldW = Math.Max(comboW, NumWidth("000"));
        var colW = labelW + fieldW + 18;
        var rowH = Theme.Mono.Height + 12;
        var perCol = (Customize.Length + 1) / 2;
        var y0 = 36;

        foreach (var f in Customize.Fields)
        {
            var col = f.Index < perCol ? 0 : 1;
            var row = f.Index < perCol ? f.Index : f.Index - perCol;
            var x = 14 + col * colW;
            var y = y0 + row * rowH;

            var label = new Label
            {
                Text = f.Name, Font = Theme.MonoSmall,
                ForeColor = f.Kind == CustomizeKind.Color ? Theme.Violet : Theme.InkDim,
                AutoSize = false, Width = labelW, Height = rowH - 4,
                Location = new Point(x, y + 3),
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
            };
            if (f.Hint.Length > 0) _tips.SetToolTip(label, f.Hint);
            host.Controls.Add(label);

            Control field;
            switch (f.Kind)
            {
                case CustomizeKind.Race:
                    Fill(_race, Customize.Races.Select(r => (r.Id, r.Name)));
                    _race.SelectedIndexChanged += (_, _) => OnRaceChanged();
                    field = _race;
                    break;
                case CustomizeKind.Gender:
                    Fill(_gender, new[] { ((byte)0, "Male"), ((byte)1, "Female") });
                    field = _gender;
                    break;
                case CustomizeKind.Clan:
                    field = _clan;
                    break;
                default:
                    var n = Spinner(255);
                    _cust[f.Index] = n;
                    field = n;
                    break;
            }
            field.Location = new Point(x + labelW + 6, y);
            field.Width = fieldW;
            if (f.Hint.Length > 0) _tips.SetToolTip(field, f.Hint);
            host.Controls.Add(field);
        }

        var note = new Label
        {
            Text = "colors are palette indices from the game's own data files, not RGB,\n" +
                   "so they can only be edited as numbers here.",
            Font = Theme.MonoSmall, ForeColor = Theme.InkFaint, UseMnemonic = false,
            AutoSize = false, Width = colW * 2 - 8, Height = Theme.MonoSmall.Height * 2 + 8,
            Location = new Point(14, y0 + perCol * rowH + 8),
        };
        host.Controls.Add(note);
        _appearanceNote = note;

        host.Size = new Size(colW * 2 + 14, note.Bottom + 12);
        return host;

        static void Fill(ComboBox box, IEnumerable<(byte Id, string Name)> items)
        {
            box.Items.Clear();
            foreach (var (id, name) in items) box.Items.Add(new Choice(id, name));
        }
    }

    private Panel BuildGear()
    {
        var host = new Panel { BackColor = Theme.Panel };
        host.Paint += (_, e) =>
        {
            Theme.DrawFrame(e.Graphics, host.ClientRectangle);
            // NoPrefix, or the ampersand is swallowed as a mnemonic underline.
            TextRenderer.DrawText(e.Graphics, "Gear & dyes", Theme.SansBold, new Point(14, 10),
                Theme.Ink, TextFormatFlags.NoPrefix);
        };

        var rowH = Theme.Mono.Height + 12;
        var labelW = CharacterLayout.GearSlotNames
            .Concat(new[] { "Main hand", "Facewear", "Title", "Current world", "Home world", "Status" })
            .Max(s => TextRenderer.MeasureText(s, Theme.MonoSmall).Width) + 10;
        int[] widths =
        {
            NumWidth("65535"), NumWidth("255"), NumWidth("255"), NumWidth("255"), NumWidth("4294967295"),
        };
        string[] headers = { "model", "variant", "dye 1", "dye 2", "portrait item" };
        var y = 38;

        var x = 14 + labelW;
        for (var c = 0; c < headers.Length; c++)
        {
            host.Controls.Add(Caption(headers[c], x, y, widths[c]));
            x += widths[c] + 6;
        }
        var rightEdge = x;
        y += Theme.MonoSmall.Height + 4;

        for (var s = 0; s < CharacterLayout.GearSlots; s++)
        {
            host.Controls.Add(SlotLabel(CharacterLayout.GearSlotNames[s], y, labelW, rowH));
            x = 14 + labelW;
            for (var c = 0; c < 5; c++)
            {
                // model is a u16, variant and dyes a byte, the portrait item id a u32
                var max = c switch { 0 => 65535m, 4 => 4294967295m, _ => 255m };
                var n = Spinner(max);
                n.SetBounds(x, y, widths[c], rowH - 4);
                // Without a portrait block for this character there is nowhere for an
                // item id to be written, so don't pretend the field does anything.
                if (c == 4 && _record.PortraitBlocks == 0)
                {
                    n.Enabled = false;
                    _tips.SetToolTip(n, "This recording has no party-portrait entry for this character.");
                }
                _gear[s, c] = n;
                host.Controls.Add(n);
                x += widths[c] + 6;
            }
            y += rowH;
        }

        y += 12;
        string[] wheaders = { "model", "base", "variant", "dye" };
        x = 14 + labelW;
        for (var c = 0; c < wheaders.Length; c++)
        {
            host.Controls.Add(Caption(wheaders[c], x, y, widths[c]));
            x += widths[c] + 6;
        }
        y += Theme.MonoSmall.Height + 4;

        for (var w = 0; w < 2; w++)
        {
            host.Controls.Add(SlotLabel(w == 0 ? "Main hand" : "Off hand", y, labelW, rowH));
            x = 14 + labelW;
            for (var c = 0; c < 4; c++)
            {
                var n = Spinner(65535);
                n.SetBounds(x, y, widths[c], rowH - 4);
                _weapon[w, c] = n;
                host.Controls.Add(n);
                x += widths[c] + 6;
            }
            y += rowH;
        }

        y += 14;
        host.Controls.Add(SlotLabel("Facewear", y, labelW, rowH));
        StyleSpinner(_facewear, 65535);
        _facewear.SetBounds(14 + labelW, y, widths[0], rowH - 4);
        _tips.SetToolTip(_facewear, "Glasses / facewear model id. 0 = none.");
        host.Controls.Add(_facewear);
        y += rowH;

        host.Controls.Add(SlotLabel("Title", y, labelW, rowH));
        StyleSpinner(_title, 65535);
        _title.SetBounds(14 + labelW, y, widths[0], rowH - 4);
        _tips.SetToolTip(_title,
            "The title shown under the character's name, as a row id in the game's " +
            "Title sheet. 0 = no title. Anonymizing clears it.");
        host.Controls.Add(_title);
        y += rowH;

        host.Controls.Add(SlotLabel("Current world", y, labelW, rowH));
        StyleSpinner(_curWorld, 65535);
        _curWorld.SetBounds(14 + labelW, y, widths[0], rowH - 4);
        _tips.SetToolTip(_curWorld,
            "The world this character is logged in on. Differs from the home world " +
            "only for someone who has travelled to another world.");
        host.Controls.Add(_curWorld);
        y += rowH;

        host.Controls.Add(SlotLabel("Home world", y, labelW, rowH));
        StyleSpinner(_homeWorld, 65535);
        _homeWorld.SetBounds(14 + labelW, y, widths[0], rowH - 4);
        _tips.SetToolTip(_homeWorld,
            "The world this character belongs to. Written to the spawn packet and to " +
            "the party roster, which keeps its own copy.");
        host.Controls.Add(_homeWorld);
        y += rowH;

        host.Controls.Add(SlotLabel("Status", y, labelW, rowH));
        _online.Font = Theme.Mono;
        _online.BackColor = Theme.Panel2;
        _online.ForeColor = Theme.Ink;
        _online.FlatStyle = FlatStyle.Flat;
        foreach (var (id, name) in OnlineStatusData.All) _online.Items.Add(new Choice(id, name));
        _online.SetBounds(14 + labelW, y,
            Math.Min(rightEdge - 20 - labelW, LongestStatusWidth()), rowH - 4);
        _tips.SetToolTip(_online,
            "The status icon beside the character's name - Busy, the mentor crowns, " +
            "and so on. Written to the spawn packet and to every ActorControl that " +
            "re-sends it later, so it holds for the whole playback. Anonymizing sets " +
            "everyone to In Duty. Away from Keyboard and Looking to Meld Materia are " +
            "listed but never recorded by the game, so setting one shows something " +
            "the original recording could not have.");
        host.Controls.Add(_online);
        y += rowH + 8;

        foreach (var (box, tip) in new[]
                 {
                     (_hideHead, "The character screen's \"hide headgear\" toggle."),
                     (_hideWeapon, "The character screen's \"hide weapon\" toggle - a weapon set here stays invisible while it is on."),
                 })
        {
            box.Font = Theme.Sans;
            box.ForeColor = Theme.Ink;
            box.BackColor = Theme.Panel;
            box.FlatStyle = FlatStyle.Flat;
            box.FlatAppearance.BorderColor = Theme.Line;
            box.AutoSize = true;
            box.Location = new Point(14, y);
            _tips.SetToolTip(box, tip);
            host.Controls.Add(box);
            y += box.Height + 6;
        }

        var note = new Label
        {
            Text = _record.PortraitBlocks == 0
                ? "Model, variant and dyes drive the in-arena character. This recording has\n" +
                  "no party-portrait entry for them, so the item id column is inert."
                : "Model, variant and dyes drive the in-arena character. The portrait item id\n" +
                  "is what the party list shows; there is no model-to-item map, so set both.",
            Font = Theme.MonoSmall, ForeColor = Theme.InkFaint, UseMnemonic = false,
            AutoSize = false, Width = rightEdge - 20, Height = Theme.MonoSmall.Height * 2 + 8,
            Location = new Point(14, y + 4),
        };
        host.Controls.Add(note);
        _gearNote = note;

        host.Size = new Size(rightEdge, note.Bottom + 12);
        return host;

        static Label Caption(string text, int x, int y, int w) => new()
        {
            Text = text, Font = Theme.MonoSmall, ForeColor = Theme.InkFaint, UseMnemonic = false,
            AutoSize = false, Width = w, Height = Theme.MonoSmall.Height + 2, Location = new Point(x, y),
        };

        static Label SlotLabel(string text, int y, int w, int rowH) => new()
        {
            Text = text, Font = Theme.MonoSmall, ForeColor = Theme.InkDim, UseMnemonic = false,
            AutoSize = false, Width = w - 6, Height = rowH - 4, Location = new Point(14, y + 3),
            TextAlign = ContentAlignment.MiddleLeft,
        };
    }

    private Panel BuildFooter()
    {
        var host = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Theme.Panel, Padding = new Padding(16, 10, 16, 10) };
        host.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Line);
            e.Graphics.DrawLine(p, 0, 0, host.Width, 0);
        };

        var ok = new FlatButton("Apply") { Accent = true };
        ok.Click += (_, _) => { Result = ReadAll(); DialogResult = DialogResult.OK; Close(); };
        var cancel = new FlatButton("Cancel");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Panel,
        };
        flow.Controls.Add(ok);
        flow.Controls.Add(cancel);

        _status.Font = Theme.MonoSmall;
        _status.ForeColor = Theme.InkDim;
        _status.AutoSize = false;
        _status.Dock = DockStyle.Left;
        _status.Width = 520;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        host.Controls.Add(_status);
        host.Controls.Add(flow);
        AcceptButton = ok;
        CancelButton = cancel;
        return host;
    }

    private readonly Label _status = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000, InitialDelay = 300, ReshowDelay = 100 };

    private void Log(string message, bool error = false)
    {
        _status.Text = message;
        _status.ForeColor = error ? Theme.Danger : Theme.InkDim;
    }

    // ---- field plumbing ---------------------------------------------------

    private sealed record Choice(byte Id, string Name)
    {
        public override string ToString() => $"{Name} ({Id})";
    }

    /// <summary>Width for the status combo, from the longest entry it can show.</summary>
    private static int LongestStatusWidth() =>
        OnlineStatusData.All.Max(e => TextRenderer.MeasureText($"{e.Name} ({e.Id})", Theme.Mono).Width) + 34;

    /// <summary>Width for a spinner that must show <paramref name="sample"/> in full,
    /// including the up/down buttons the control adds.</summary>
    private static int NumWidth(string sample) =>
        TextRenderer.MeasureText(sample, Theme.Mono).Width + SystemInformation.VerticalScrollBarWidth + 12;

    private NumericUpDown Spinner(decimal max)
    {
        var n = new NumericUpDown();
        StyleSpinner(n, max);
        return n;
    }

    private static void StyleSpinner(NumericUpDown n, decimal max)
    {
        n.Minimum = 0;
        n.Maximum = max;
        n.Font = Theme.Mono;
        n.BackColor = Theme.Panel2;
        n.ForeColor = Theme.Ink;
        n.BorderStyle = BorderStyle.FixedSingle;
        n.TextAlign = HorizontalAlignment.Right;
    }

    private void OnRaceChanged()
    {
        if (_syncingRace) return;
        var race = ((Choice?)_race.SelectedItem)?.Id ?? 1;
        var keep = ((Choice?)_clan.SelectedItem)?.Id ?? 0;
        _clan.Items.Clear();
        foreach (var (id, name) in Customize.ClansOf(race)) _clan.Items.Add(new Choice(id, name));
        var idx = _clan.Items.Cast<Choice>().ToList().FindIndex(c => c.Id == keep);
        _clan.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private static void Select(ComboBox box, byte id)
    {
        var idx = box.Items.Cast<Choice>().ToList().FindIndex(c => c.Id == id);
        if (idx >= 0) { box.SelectedIndex = idx; return; }
        // A value the game shouldn't produce is still in the file; keep it visible.
        box.Items.Add(new Choice(id, "unknown"));
        box.SelectedIndex = box.Items.Count - 1;
    }

    private void LoadAppearance(CharacterAppearance a)
    {
        WriteCustomize(a.Customize);
        for (var s = 0; s < CharacterLayout.GearSlots; s++)
        {
            _gear[s, 0].Value = a.Gear[s].Model;
            _gear[s, 1].Value = a.Gear[s].Variant;
            _gear[s, 2].Value = a.Gear[s].Dye1;
            _gear[s, 3].Value = a.Gear[s].Dye2;
            _gear[s, 4].Value = a.Gear[s].PortraitItemId;
        }
        LoadWeapon(0, a.MainHand);
        LoadWeapon(1, a.OffHand);
        _facewear.Value = a.Facewear;
        _title.Value = a.Title;
        _curWorld.Value = a.CurrentWorld;
        _homeWorld.Value = a.HomeWorld;
        Select(_online, a.OnlineStatus);
        _hideHead.Checked = a.HideHeadgear;
        _hideWeapon.Checked = a.HideWeapon;

        void LoadWeapon(int w, WeaponPiece p)
        {
            _weapon[w, 0].Value = p.Model;
            _weapon[w, 1].Value = p.Base;
            _weapon[w, 2].Value = p.Variant;
            _weapon[w, 3].Value = p.Dye;
        }
    }

    private void WriteCustomize(Customize c)
    {
        _syncingRace = true;
        Select(_race, c.Race);
        Select(_gender, c.Gender);
        _clan.Items.Clear();
        foreach (var (id, name) in Customize.ClansOf(c.Race)) _clan.Items.Add(new Choice(id, name));
        _syncingRace = false;
        Select(_clan, c.Clan);

        for (var i = 0; i < Customize.Length; i++)
            if (_cust[i] is { } n) n.Value = c[i];
    }

    private Customize ReadCustomize()
    {
        var c = new Customize();
        for (var i = 0; i < Customize.Length; i++)
            if (_cust[i] is { } n) c[i] = (byte)n.Value;
        c.Race = ((Choice?)_race.SelectedItem)?.Id ?? 1;
        c.Gender = ((Choice?)_gender.SelectedItem)?.Id ?? 0;
        c.Clan = ((Choice?)_clan.SelectedItem)?.Id ?? 1;
        return c;
    }

    private CharacterAppearance ReadAll()
    {
        var gear = new GearPiece[CharacterLayout.GearSlots];
        for (var s = 0; s < gear.Length; s++)
            gear[s] = new GearPiece
            {
                Model = (ushort)_gear[s, 0].Value,
                Variant = (byte)_gear[s, 1].Value,
                Dye1 = (byte)_gear[s, 2].Value,
                Dye2 = (byte)_gear[s, 3].Value,
                PortraitItemId = (uint)_gear[s, 4].Value,
            };
        return new CharacterAppearance
        {
            Customize = ReadCustomize(),
            Gear = gear,
            MainHand = ReadWeapon(0),
            OffHand = ReadWeapon(1),
            Facewear = (ushort)_facewear.Value,
            Title = (ushort)_title.Value,
            CurrentWorld = (ushort)_curWorld.Value,
            HomeWorld = (ushort)_homeWorld.Value,
            OnlineStatus = ((Choice?)_online.SelectedItem)?.Id ?? OnlineStatusData.InDuty,
            HideHeadgear = _hideHead.Checked,
            HideWeapon = _hideWeapon.Checked,
        };

        WeaponPiece ReadWeapon(int w) => new()
        {
            Model = (ushort)_weapon[w, 0].Value,
            Base = (ushort)_weapon[w, 1].Value,
            Variant = (ushort)_weapon[w, 2].Value,
            Dye = (ushort)_weapon[w, 3].Value,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
