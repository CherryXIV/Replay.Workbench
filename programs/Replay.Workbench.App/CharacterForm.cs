using ReplayWorkbench.Core;

namespace ReplayWorkbench.App;

/// <summary>
/// Per-character appearance editor: the customize block, gear and dyes, weapons,
/// facewear, title, worlds, status icon and the display toggles, for one player in
/// the recording.
/// </summary>
/// <remarks>
/// Every control lives in CharacterForm.Designer.cs and is editable in the
/// WinForms designer, the two grids included: the customize fields and the gear
/// table are laid out in auto-sizing TableLayoutPanels, so they measure themselves
/// rather than needing the pixel arithmetic this used to do.  What is left here is
/// behaviour - filling the drop-downs from the game's data, the per-field
/// tooltips, and moving values in and out of the controls.
/// </remarks>
internal sealed partial class CharacterForm : Form
{
    private readonly CharacterRecord _record;

    /// <summary>
    /// The customize spinners by field index, and the gear and weapon spinners by
    /// [slot, column].  These are the designer's own controls, found by name -
    /// see <see cref="BindGrids"/> for the naming contract.
    /// </summary>
    private readonly NumericUpDown?[] _cust = new NumericUpDown?[Customize.Length];
    private readonly NumericUpDown[,] _gear = new NumericUpDown[CharacterLayout.GearSlots, 5];
    private readonly NumericUpDown[,] _weapon = new NumericUpDown[2, 4];

    private bool _syncingRace;

    /// <summary>The edited appearance, valid once the dialog returns OK.</summary>
    public CharacterAppearance Result { get; private set; }

    public CharacterForm(CharacterRecord record, CharacterAppearance current)
    {
        _record = record;
        Result = current.Clone();

        InitializeComponent();

        Text = $"Character Editor: {record.Name}";
        nameLabel.Text = $"{record.Name}  ·  {record.JobName}";
        subLabel.Text = $"key 0x{record.CharacterKey:x16}  -  {record.SpawnPackets} spawn packet" +
                        $"{(record.SpawnPackets == 1 ? "" : "s")}, {record.PortraitBlocks} portrait block" +
                        $"{(record.PortraitBlocks == 1 ? "" : "s")}";
        btnJobGear.Text = $"Dress in {record.JobName} artifact gear";

        BindGrids();
        FillCombos();
        AnnotateFields();

        // Without a portrait block for this character there is nowhere for an item
        // id to be written, so don't pretend the field does anything.
        if (record.PortraitBlocks == 0)
        {
            gearNote.Text = "Model, variant and dyes drive the in-arena character. This recording has\n" +
                            "no party-portrait entry for them, so the item id column is inert.";
            for (var slot = 0; slot < CharacterLayout.GearSlots; slot++)
            {
                _gear[slot, 4].Enabled = false;
                tips.SetToolTip(_gear[slot, 4], "This recording has no party-portrait entry for this character.");
            }
        }

        FitLayout();
        LoadAppearance(current);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
    }

    // ---- designer-wired events --------------------------------------------

    private void OnCopyLookClick(object? sender, EventArgs e)
    {
        Clipboard.SetText(ReadCustomize().ToHex());
        Log("Customize block copied - paste it into another character.");
    }

    private void OnPasteLookClick(object? sender, EventArgs e)
    {
        var c = Customize.FromHex(Clipboard.ContainsText() ? Clipboard.GetText() : "");
        if (c is null) { Log("Clipboard doesn't hold a 26-byte customize block.", true); return; }
        WriteCustomize(c);
        Log("Pasted a customize block.");
    }

    private void OnJobGearClick(object? sender, EventArgs e)
    {
        var g = OpcodeData.GearForJob(_record.Job);
        if (g is null) { Log($"No artifact gear set known for job {_record.Job}.", true); return; }
        var a = ReadAll();
        a.ApplyJobGear(g);
        LoadAppearance(a);
        Log("Applied artifact gear.");
    }

    private void OnRevertClick(object? sender, EventArgs e)
    {
        LoadAppearance(_record.Original);
        Log("Reverted to the recording's own values.");
    }

    private void OnApplyClick(object? sender, EventArgs e)
    {
        Result = ReadAll();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitLayout();
    }

    /// <summary>
    /// Size the window to whatever the cards came out to.  Everything inside them
    /// auto-sizes, so this only has to ask - and clamp: never grow past the screen
    /// the dialog is on, since the root panel scrolls.
    /// </summary>
    private void FitLayout()
    {
        // Settle the whole tree first: each card sizes itself from its content and
        // that content from its own, so a stale measurement anywhere down the chain
        // comes out as a window too small for what is in it.
        PerformLayout();
        var content = cardsLayout.GetPreferredSize(new Size(int.MaxValue, int.MaxValue));

        var want = new Size(
            content.Width + root.Padding.Horizontal,
            content.Height + root.Padding.Vertical + footer.Height);
        var screen = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min(want.Width, screen.Width - 80),
                              Math.Min(want.Height, screen.Height - 80));
    }

    /// <summary>
    /// Pick the generated grids' controls out of the designer by name.  The naming
    /// is the contract between the two files: customize spinners are cust{index},
    /// gear is gear{slot}_{column} and weapons weapon{hand}_{column}.  Renaming one
    /// in the designer breaks the binding, so it says so rather than crashing later.
    /// </summary>
    private void BindGrids()
    {
        for (var i = 0; i < Customize.Length; i++)
            _cust[i] = appearanceGrid.Controls[$"cust{i}"] as NumericUpDown;

        for (var slot = 0; slot < CharacterLayout.GearSlots; slot++)
            for (var col = 0; col < 5; col++)
                _gear[slot, col] = Spinner(gearGrid, $"gear{slot}_{col}");

        for (var hand = 0; hand < 2; hand++)
            for (var col = 0; col < 4; col++)
                _weapon[hand, col] = Spinner(weaponGrid, $"weapon{hand}_{col}");

        static NumericUpDown Spinner(Control host, string name) =>
            host.Controls[name] as NumericUpDown
            ?? throw new InvalidOperationException(
                $"CharacterForm.Designer.cs has no NumericUpDown called '{name}' - " +
                "the grid binding goes by name, so it can't be renamed there.");
    }

    /// <summary>Fill the drop-downs that are backed by the game's data.</summary>
    private void FillCombos()
    {
        foreach (var (id, name) in Customize.Races) racePick.Items.Add(new Choice(id, name));
        foreach (var (id, name) in new[] { ((byte)0, "Male"), ((byte)1, "Female") })
            genderPick.Items.Add(new Choice(id, name));
        foreach (var (id, name) in OnlineStatusData.All) onlinePick.Items.Add(new Choice(id, name));
        racePick.SelectedIndexChanged += (_, _) => OnRaceChanged();
    }

    /// <summary>Hang the per-field hints off the labels and fields they explain.</summary>
    private void AnnotateFields()
    {
        foreach (var f in Customize.Fields)
        {
            if (f.Hint.Length == 0) continue;
            if (appearanceGrid.Controls[$"custLabel{f.Index}"] is { } label) tips.SetToolTip(label, f.Hint);
            if (_cust[f.Index] is { } field) tips.SetToolTip(field, f.Hint);
        }

        tips.SetToolTip(facewearBox, "Glasses / facewear model id. 0 = none.");
        tips.SetToolTip(titleBox,
            "The title shown under the character's name, as a row id in the game's " +
            "Title sheet. 0 = no title. Anonymizing clears it.");
        tips.SetToolTip(curWorldBox,
            "The world this character is logged in on. Differs from the home world " +
            "only for someone who has travelled to another world.");
        tips.SetToolTip(homeWorldBox,
            "The world this character belongs to. Written to the spawn packet and to " +
            "the party roster, which keeps its own copy.");
        tips.SetToolTip(onlinePick,
            "The status icon beside the character's name - Busy, the mentor crowns, " +
            "and so on. Written to the spawn packet and to every ActorControl that " +
            "re-sends it later, so it holds for the whole playback. Anonymizing sets " +
            "everyone to In Duty. Away from Keyboard and Looking to Meld Materia are " +
            "listed but never recorded by the game, so setting one shows something " +
            "the original recording could not have.");
        tips.SetToolTip(hideHeadBox, "The character screen's \"hide headgear\" toggle.");
        tips.SetToolTip(hideWeaponBox,
            "The character screen's \"hide weapon\" toggle - a weapon set here stays invisible while it is on.");
    }

    // ---- chrome -----------------------------------------------------------

    private void Log(string message, bool error = false)
    {
        statusLabel.Text = message;
        statusLabel.ForeColor = error ? Theme.Danger : Theme.InkDim;
    }

    // ---- field plumbing ---------------------------------------------------

    private sealed record Choice(byte Id, string Name)
    {
        public override string ToString() => $"{Name} ({Id})";
    }

    private void OnRaceChanged()
    {
        if (_syncingRace) return;
        var race = ((Choice?)racePick.SelectedItem)?.Id ?? 1;
        var keep = ((Choice?)clanPick.SelectedItem)?.Id ?? 0;
        clanPick.Items.Clear();
        foreach (var (id, name) in Customize.ClansOf(race)) clanPick.Items.Add(new Choice(id, name));
        var idx = clanPick.Items.Cast<Choice>().ToList().FindIndex(c => c.Id == keep);
        clanPick.SelectedIndex = idx >= 0 ? idx : 0;
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
        facewearBox.Value = a.Facewear;
        titleBox.Value = a.Title;
        curWorldBox.Value = a.CurrentWorld;
        homeWorldBox.Value = a.HomeWorld;
        Select(onlinePick, a.OnlineStatus);
        hideHeadBox.Checked = a.HideHeadgear;
        hideWeaponBox.Checked = a.HideWeapon;

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
        Select(racePick, c.Race);
        Select(genderPick, c.Gender);
        clanPick.Items.Clear();
        foreach (var (id, name) in Customize.ClansOf(c.Race)) clanPick.Items.Add(new Choice(id, name));
        _syncingRace = false;
        Select(clanPick, c.Clan);

        for (var i = 0; i < Customize.Length; i++)
            if (_cust[i] is { } n) n.Value = c[i];
    }

    private Customize ReadCustomize()
    {
        var c = new Customize();
        for (var i = 0; i < Customize.Length; i++)
            if (_cust[i] is { } n) c[i] = (byte)n.Value;
        c.Race = ((Choice?)racePick.SelectedItem)?.Id ?? 1;
        c.Gender = ((Choice?)genderPick.SelectedItem)?.Id ?? 0;
        c.Clan = ((Choice?)clanPick.SelectedItem)?.Id ?? 1;
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
            Facewear = (ushort)facewearBox.Value,
            Title = (ushort)titleBox.Value,
            CurrentWorld = (ushort)curWorldBox.Value,
            HomeWorld = (ushort)homeWorldBox.Value,
            OnlineStatus = ((Choice?)onlinePick.SelectedItem)?.Id ?? OnlineStatusData.InDuty,
            HideHeadgear = hideHeadBox.Checked,
            HideWeapon = hideWeaponBox.Checked,
        };

        WeaponPiece ReadWeapon(int w) => new()
        {
            Model = (ushort)_weapon[w, 0].Value,
            Base = (ushort)_weapon[w, 1].Value,
            Variant = (ushort)_weapon[w, 2].Value,
            Dye = (ushort)_weapon[w, 3].Value,
        };
    }
}
