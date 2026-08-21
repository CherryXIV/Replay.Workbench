using System.ComponentModel;
using System.Drawing.Drawing2D;
using ReplayWorkbench.Core;

namespace ReplayWorkbench.App;

// Everything in this file is public with a parameterless constructor on purpose:
// that is what the WinForms designer needs in order to instantiate a control on
// the design surface and list it in the toolbox.  Each one paints itself in the
// workbench palette from its own constructor, so the design surface shows the
// dark theme without any colours being baked into the .Designer.cs files.

/// <summary>
/// A titled panel: header strip with a name on the left and a meta note on the
/// right.  The header is reserved by the panel's own <see cref="Control.Padding"/>,
/// so a Dock=Fill child lands underneath it and the designer shows the same
/// arrangement the running app does.
/// </summary>
public sealed class CardPanel : Panel
{
    public const int HeadHeight = 30;

    private string _title = "Card";
    private string _meta = "";
    private bool _autoSizeToContent;
    private int _gapBelow;

    public CardPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Ink;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Padding = new Padding(1, HeadHeight, 1, 1);
        Size = new Size(420, HeadHeight + 90);
    }

    public CardPanel(string title) : this() => _title = title;

    [Category("Card"), DefaultValue("Card")]
    public string Title
    {
        get => _title;
        set { _title = value ?? ""; InvalidateHeader(); }
    }

    /// <summary>The dim note printed at the right end of the header strip.</summary>
    [Category("Card"), DefaultValue("")]
    public string Meta
    {
        get => _meta;
        set { _meta = value ?? ""; InvalidateHeader(); }
    }

    /// <summary>
    /// Take the card's height from its body instead of keeping the height set in
    /// the designer.  For cards whose content decides its own height (the readout
    /// reflows, the timeline is a fixed strip); the body control should be Dock=Top.
    /// </summary>
    [Category("Card"), DefaultValue(false)]
    public bool AutoSizeToContent
    {
        get => _autoSizeToContent;
        set { _autoSizeToContent = value; FitContent(); }
    }

    /// <summary>
    /// Blank space left below the card, inside its own height.  The cards stack by
    /// Dock=Top, and WinForms ignores Margin on a docked control, so the gap between
    /// two cards has to belong to one of them - it is drawn in the parent's colour
    /// and the card's frame stops short of it.
    /// </summary>
    [Category("Card"), DefaultValue(0)]
    public int GapBelow
    {
        get => _gapBelow;
        set
        {
            var delta = Math.Max(0, value) - _gapBelow;
            _gapBelow = Math.Max(0, value);
            Height += delta;
            Invalidate();
        }
    }

    /// <summary>The card without its <see cref="GapBelow"/> - what actually gets painted.</summary>
    private int FrameHeight => Math.Max(1, Height - _gapBelow);

    /// <summary>Size the card so its body gets exactly <paramref name="bodyHeight"/> pixels.</summary>
    public void SetBodyHeight(int bodyHeight) =>
        Height = Padding.Top + Math.Max(0, bodyHeight) + Padding.Bottom + _gapBelow;

    /// <summary>Re-take the height from the tallest visible child. No-op unless
    /// <see cref="AutoSizeToContent"/> is on.</summary>
    public void FitContent()
    {
        if (!_autoSizeToContent) return;
        var want = 0;
        foreach (Control c in Controls)
            if (c.Visible)
                want = Math.Max(want, c.Height);
        if (want > 0) SetBodyHeight(want);
    }

    private void InvalidateHeader() => Invalidate(new Rectangle(0, 0, Width, HeadHeight));

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is not null) e.Control.SizeChanged += OnChildSized;
        FitContent();
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        if (e.Control is not null) e.Control.SizeChanged -= OnChildSized;
        base.OnControlRemoved(e);
    }

    private void OnChildSized(object? sender, EventArgs e) => FitContent();

    public override Rectangle DisplayRectangle
    {
        get
        {
            var r = base.DisplayRectangle;
            return new Rectangle(r.X, r.Y, r.Width, Math.Max(0, r.Height - _gapBelow));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var frame = FrameHeight;
        if (_gapBelow > 0)
            using (var b = new SolidBrush(Parent?.BackColor ?? Theme.Bg))
                g.FillRectangle(b, 0, frame, Width, Height - frame);
        using (var b = new SolidBrush(Theme.Panel2))
            g.FillRectangle(b, 1, 1, Width - 2, HeadHeight - 1);
        using (var p = new Pen(Theme.Line))
        {
            g.DrawRectangle(p, 0, 0, Width - 1, frame - 1);
            g.DrawLine(p, 1, HeadHeight - 1, Width - 2, HeadHeight - 1);
        }
        TextRenderer.DrawText(g, _title, Theme.SansBold, new Point(14, 8), Theme.Ink,
            TextFormatFlags.NoPrefix);
        if (_meta.Length > 0)
        {
            var size = TextRenderer.MeasureText(g, _meta, Theme.MonoSmall);
            TextRenderer.DrawText(g, _meta, Theme.MonoSmall,
                new Point(Width - 14 - size.Width, 9), Theme.InkDim, TextFormatFlags.NoPrefix);
        }
        base.OnPaint(e);
    }
}

/// <summary>Which edge a <see cref="RulePanel"/> draws its hairline on.</summary>
public enum RuleEdge
{
    None,
    Top,
    Bottom,
}

/// <summary>A themed panel with an optional hairline along one edge - the separator
/// under the log strip and above the character editor's footer.</summary>
public sealed class RulePanel : Panel
{
    private RuleEdge _edge = RuleEdge.Top;

    public RulePanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Ink;
        ResizeRedraw = true;
    }

    [Category("Card"), DefaultValue(RuleEdge.Top)]
    public RuleEdge Edge
    {
        get => _edge;
        set { _edge = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_edge == RuleEdge.None) return;
        var y = _edge == RuleEdge.Top ? 0 : Height - 1;
        using var p = new Pen(Theme.Line);
        e.Graphics.DrawLine(p, 0, y, Width, y);
    }
}

/// <summary>A panel that draws the workbench's plain 1px frame around itself.</summary>
public sealed class FramedPanel : Panel
{
    public FramedPanel()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Ink;
        ResizeRedraw = true;
    }

    /// <summary>
    /// Size to what the content wants rather than to what it currently measures.
    /// A Panel's stock AutoSize reads its children's <em>bounds</em>, which for a
    /// Dock=Fill child are in turn taken from the panel - the two starve each other
    /// down to nothing.  Asking the child for its own preferred size breaks that,
    /// and lets a card wrap an auto-sizing table without any pixel arithmetic.
    /// </summary>
    private static readonly Size Unconstrained = new(int.MaxValue, int.MaxValue);

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (!AutoSize) return base.GetPreferredSize(proposedSize);
        int w = 0, h = 0;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            var want = c.GetPreferredSize(Unconstrained);
            w = Math.Max(w, want.Width + c.Margin.Horizontal);
            h = Math.Max(h, want.Height + c.Margin.Vertical);
        }
        return w == 0 && h == 0
            ? base.GetPreferredSize(proposedSize)
            : new Size(w + Padding.Horizontal, h + Padding.Vertical);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Theme.DrawFrame(e.Graphics, ClientRectangle);
    }
}

/// <summary>The "drop a recording here" target: a label in a dashed frame.</summary>
public sealed class DropHintLabel : Label
{
    private int _gapBelow;

    public DropHintLabel()
    {
        BackColor = Theme.Bg;
        ForeColor = Theme.InkDim;
        Font = Theme.Mono;
        TextAlign = ContentAlignment.MiddleCenter;
        Cursor = Cursors.Hand;
        AutoSize = false;
        Height = 92;
    }

    /// <summary>Blank space below the dashed frame, inside the control's own height -
    /// same trick as <see cref="CardPanel.GapBelow"/>, for the same reason.</summary>
    [Category("Card"), DefaultValue(0)]
    public int GapBelow
    {
        get => _gapBelow;
        set
        {
            var delta = Math.Max(0, value) - _gapBelow;
            _gapBelow = Math.Max(0, value);
            Height += delta;
            // keeps the caption centred on the framed part, not on the gap
            Padding = new Padding(Padding.Left, Padding.Top, Padding.Right, _gapBelow);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(Theme.Line) { DashStyle = DashStyle.Dash };
        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Math.Max(1, Height - _gapBelow) - 1);
    }
}

/// <summary>The app menu bar, dark-rendered. A control rather than four lines of
/// setup in every form, so the designer shows it themed.</summary>
public sealed class DarkMenuStrip : MenuStrip
{
    public DarkMenuStrip()
    {
        BackColor = Theme.Panel2;
        ForeColor = Theme.Ink;
        Renderer = new ToolStripProfessionalRenderer(new DarkColors());
        Padding = new Padding(6, 2, 0, 2);
    }

    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);
        if (e.Item is ToolStripMenuItem m) Recolour(m);
    }

    /// <summary>Re-apply the palette to every item, including drop-downs built after
    /// the strip was created (the designer adds children item by item).</summary>
    public void Recolour()
    {
        foreach (var item in Items)
            if (item is ToolStripMenuItem m)
                Recolour(m);
    }

    private static void Recolour(ToolStripMenuItem item)
    {
        item.BackColor = Theme.Panel2;
        item.ForeColor = Theme.Ink;
        foreach (var child in item.DropDownItems)
            if (child is ToolStripMenuItem m) Recolour(m);
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
}

/// <summary>A dark, flat, monospaced text box - the dialogs' input style.</summary>
public sealed class DarkTextBox : TextBox
{
    public DarkTextBox()
    {
        BackColor = Theme.Panel2;
        ForeColor = Theme.Ink;
        Font = Theme.Mono;
        BorderStyle = BorderStyle.FixedSingle;
    }
}

/// <summary>A dark, flat, monospaced drop-down - the forms' picker style.</summary>
public sealed class DarkComboBox : ComboBox
{
    public DarkComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        BackColor = Theme.Panel2;
        ForeColor = Theme.Ink;
        FlatStyle = FlatStyle.Flat;
        Font = Theme.Mono;
    }
}

/// <summary>A dark, right-aligned numeric spinner - every id field in the app.</summary>
public sealed class DarkNumeric : NumericUpDown
{
    public DarkNumeric()
    {
        Minimum = 0;
        Maximum = 255;
        Font = Theme.Mono;
        BackColor = Theme.Panel2;
        ForeColor = Theme.Ink;
        BorderStyle = BorderStyle.FixedSingle;
        TextAlign = HorizontalAlignment.Right;
    }
}

/// <summary>A dim monospaced caption - the small explanatory lines under a card.</summary>
public sealed class HintLabel : Label
{
    public HintLabel()
    {
        Font = Theme.MonoSmall;
        ForeColor = Theme.InkDim;
        BackColor = Theme.Panel;
        AutoSize = false;
        UseMnemonic = false;
    }
}

/// <summary>
/// The recording header: a responsive grid of label-over-value cells, painted
/// rather than laid out so it reflows with the window without flicker.
/// </summary>
public sealed class ReadoutView : Control
{
    private IReadOnlyList<(string Key, string Value, ReadoutTone Tone)> _cells = [];

    public ReadoutView()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Panel;
        Height = DesignHeight;
    }

    public void SetCells(IReadOnlyList<(string, string, ReadoutTone)> cells)
    {
        _cells = cells;
        Reflow();
        Invalidate();
    }

    private const int MinCellW = 210;
    private const int Pad = 14;
    private const int Gap = 10;

    private int KeyH => Theme.MonoSmall.Height;
    private int ValueH => Theme.Mono.Height;
    private int CellH => KeyH + ValueH + 14;

    /// <summary>Two rows' worth, so the card is not a sliver on the design surface.</summary>
    private int DesignHeight => Pad * 2 + CellH * 2;

    private int Columns => Math.Max(1, (Width - Pad * 2 + Gap) / (MinCellW + Gap));

    private void Reflow()
    {
        if (_cells.Count == 0)
        {
            // Empty at design time means "no file loaded", which at runtime is a
            // hidden card - but on the design surface it has to be visible.
            Height = DesignMode ? DesignHeight : 0;
            return;
        }
        var rows = (int)Math.Ceiling(_cells.Count / (double)Columns);
        Height = Pad * 2 + rows * CellH;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Reflow();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        if (_cells.Count == 0 && DesignMode)
        {
            TextRenderer.DrawText(g, "(recording header)", Theme.MonoSmall,
                new Point(Pad, Pad), Theme.InkFaint);
            return;
        }
        var cols = Columns;
        var colW = (Width - Pad * 2 - (cols - 1) * Gap) / Math.Max(1, cols);
        for (var i = 0; i < _cells.Count; i++)
        {
            var (key, value, tone) = _cells[i];
            var x = Pad + i % cols * (colW + Gap);
            var y = Pad + i / cols * CellH;
            TextRenderer.DrawText(g, key, Theme.MonoSmall, new Rectangle(x, y, colW, KeyH),
                Theme.InkFaint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, value, Theme.Mono, new Rectangle(x, y + KeyH + 2, colW, ValueH),
                Theme.Tone(tone), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>
/// The pull timeline: the recording rendered as a track of selectable segments,
/// the way you'd scrub an oscilloscope capture, with waymark placements flagged
/// above it and clock ticks below.
/// </summary>
public sealed class TimelineControl : Control
{
    public sealed record Band(int Index, int Number, uint StartMs, uint EndMs);

    private IReadOnlyList<Band> _bands = [];
    private IReadOnlyList<uint> _waymarks = [];
    private uint _totalMs = 1;
    private int _selected = -1;
    private int _hover = -1;

    private readonly ToolTip _tip = new() { ShowAlways = true, InitialDelay = 250, ReshowDelay = 100 };

    public event EventHandler<int>? PullSelected;

    private const int FlagRow = 12;
    private const int TrackTop = 18;
    private const int TrackH = 28;

    public TimelineControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Panel;
        Height = TrackTop + TrackH + 24;
        Cursor = Cursors.Hand;
    }

    public void SetData(IReadOnlyList<Band> bands, IReadOnlyList<uint> waymarks, uint totalMs)
    {
        _bands = bands;
        _waymarks = waymarks;
        _totalMs = Math.Max(1, totalMs);
        _selected = -1;
        _hover = -1;
        Invalidate();
    }

    public void Clear() => SetData([], [], 1);

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    private int TrackLeft => 14;
    private int TrackWidth => Math.Max(1, Width - 28);
    private int XOf(uint ms) => TrackLeft + (int)(ms / (double)_totalMs * TrackWidth);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // the empty track
        using (var b = new SolidBrush(Theme.Bg))
            g.FillRectangle(b, TrackLeft, TrackTop, TrackWidth, TrackH);
        using (var p = new Pen(Theme.Line))
            g.DrawRectangle(p, TrackLeft, TrackTop, TrackWidth - 1, TrackH - 1);

        // pull segments
        foreach (var band in _bands)
        {
            var x0 = XOf(band.StartMs);
            var x1 = Math.Max(x0 + 2, XOf(band.EndMs));
            var selected = band.Index == _selected;
            var hovered = band.Index == _hover;
            var fill = selected ? Theme.Phosphor : hovered ? Theme.PhosphorDeep : Theme.PhosphorDeep;
            using (var b = new SolidBrush(hovered && !selected ? ControlPaint.Light(fill, 0.25f) : fill))
                g.FillRectangle(b, x0, TrackTop + 1, x1 - x0 - 1, TrackH - 2);
            if (!selected) continue;
            using var pen = new Pen(Theme.Phosphor, 2);
            g.DrawRectangle(pen, x0, TrackTop, x1 - x0 - 1, TrackH - 1);
        }

        // waymark flags above the track
        using (var b = new SolidBrush(Theme.Violet))
            foreach (var ms in _waymarks)
            {
                var x = XOf(ms);
                g.FillRectangle(b, x, FlagRow - 6, 2, 8);
            }

        // clock ticks below, every third pull, like the web timeline
        for (var i = 0; i < _bands.Count; i += 3)
        {
            var x = XOf(_bands[i].StartMs);
            using (var p = new Pen(Theme.InkFaint))
                g.DrawLine(p, x, TrackTop + TrackH, x, TrackTop + TrackH + 4);
            var label = Display.Clock(_bands[i].StartMs);
            var dot = label.IndexOf('.');
            if (dot >= 0) label = label[..dot];
            TextRenderer.DrawText(g, label, Theme.MonoSmall,
                new Point(x - 2, TrackTop + TrackH + 5), Theme.InkFaint);
        }
    }

    private int HitTest(Point p)
    {
        if (p.Y < TrackTop - 4 || p.Y > TrackTop + TrackH + 4) return -1;
        foreach (var band in _bands)
        {
            var x0 = XOf(band.StartMs);
            var x1 = Math.Max(x0 + 2, XOf(band.EndMs));
            if (p.X >= x0 && p.X < x1) return band.Index;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (hit == _hover) return;
        _hover = hit;
        _tip.SetToolTip(this, hit >= 0
            ? $"Pull {_bands[hit].Number} - {Display.Clock(_bands[hit].StartMs)}"
            : "");
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var hit = HitTest(e.Location);
        if (hit >= 0) PullSelected?.Invoke(this, hit);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A dark-drawn details ListView - WinForms won't theme its own header.</summary>
public sealed class DarkListView : ListView
{
    public DarkListView()
    {
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = false;
        OwnerDraw = true;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Panel;
        ForeColor = Theme.Ink;
        Font = Theme.Mono;
        DoubleBuffered = true;
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using (var b = new SolidBrush(Theme.Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
        using (var p = new Pen(Theme.Line))
            e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Theme.MonoSmall,
            Rectangle.Inflate(e.Bounds, -8, 0), Theme.InkDim,
            TextFormatFlags.VerticalCenter | Align(e.Header?.TextAlign ?? HorizontalAlignment.Left));
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e) => e.DrawDefault = false;

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var selected = e.Item?.Selected ?? false;
        var bg = selected ? Theme.Panel2 : Theme.Panel;
        using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
        if (selected && e.ColumnIndex == 0)
            using (var b = new SolidBrush(Theme.Phosphor))
                e.Graphics.FillRectangle(b, e.Bounds.Left, e.Bounds.Top, 2, e.Bounds.Height);

        var dim = e.SubItem?.Tag as string == "dim";
        var fore = selected ? Theme.Phosphor : dim ? Theme.InkDim : Theme.Ink;
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", Theme.Mono,
            Rectangle.Inflate(e.Bounds, -8, 0), fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            Align(Columns[e.ColumnIndex].TextAlign));
    }

    private static TextFormatFlags Align(HorizontalAlignment a) => a switch
    {
        HorizontalAlignment.Right => TextFormatFlags.Right,
        HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
        _ => TextFormatFlags.Left,
    };
}

/// <summary>Flat dark button; <see cref="Accent"/> makes it the primary action.</summary>
public sealed class FlatButton : Button
{
    private bool _accent;
    private bool _autoFit = true;

    public FlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        Font = Theme.Sans;
        AutoSize = false;
        Margin = new Padding(0, 0, 12, 0);
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Size = new Size(110, 30);
        Repaint();
    }

    public FlatButton(string text) : this()
    {
        Text = text;
        FitText();
    }

    /// <summary>Primary-action styling: phosphor text on a deep teal face.</summary>
    [Category("Appearance"), DefaultValue(false)]
    public bool Accent
    {
        get => _accent;
        set { _accent = value; Repaint(); }
    }

    /// <summary>
    /// Shrink-wrap the button around its caption.  Turn this off (the designer files
    /// do) when the size set in the designer is the size that should stick - otherwise
    /// the button re-measures itself on every text or font change and the designer's
    /// number is thrown away.
    /// </summary>
    [Category("Layout"), DefaultValue(true)]
    public bool AutoFit
    {
        get => _autoFit;
        set { _autoFit = value; FitText(); }
    }

    private void FitText()
    {
        if (!_autoFit) return;
        Size = FittedSize();
    }

    private Size FittedSize()
    {
        var s = TextRenderer.MeasureText(Text, Font);
        return new Size(s.Width + 32, s.Height + 16);
    }

    /// <summary>
    /// Answer with the shrink-wrapped size while <see cref="AutoFit"/> is on, so a
    /// flow or table panel measuring this button gets the size it is going to end
    /// up at rather than whatever it happens to be before the handle exists.
    /// </summary>
    public override Size GetPreferredSize(Size proposedSize) =>
        _autoFit ? FittedSize() : base.GetPreferredSize(proposedSize);

    private void Repaint()
    {
        FlatAppearance.BorderColor = _accent ? Theme.PhosphorDeep : Theme.Line;
        FlatAppearance.MouseOverBackColor = _accent ? Theme.PhosphorDeep : Theme.Panel2;
        FlatAppearance.MouseDownBackColor = Theme.Panel2;
        BackColor = _accent ? Color.FromArgb(24, 52, 56) : Theme.Panel;
        ForeColor = Enabled ? (_accent ? Theme.Phosphor : Theme.Ink) : Theme.InkFaint;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        FitText();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        FitText();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FitText();
        Repaint();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Repaint();
    }
}

/// <summary>Checkbox with a bold caption and a dim explanatory line beneath it.</summary>
public sealed class OptionCheck : Panel
{
    private readonly Label _sub;

    public OptionCheck()
    {
        BackColor = Theme.Panel;
        Box = new CheckBox
        {
            Font = Theme.Sans,
            ForeColor = Theme.Ink,
            BackColor = Theme.Panel,
            FlatStyle = FlatStyle.Flat,
            AutoSize = true,
            Location = new Point(0, 0),
            // captions are prose, so an ampersand is an ampersand, not a mnemonic
            UseMnemonic = false,
        };
        Box.FlatAppearance.BorderColor = Theme.Line;
        _sub = new Label
        {
            Font = Theme.MonoSmall,
            ForeColor = Theme.InkDim,
            AutoSize = false,
            BackColor = Theme.Panel,
            UseMnemonic = false,
            Height = Theme.MonoSmall.Height + 2,
        };
        Controls.Add(Box);
        Controls.Add(_sub);
        Box.SizeChanged += (_, _) => Arrange();
        Resize += (_, _) => Arrange();
        Width = 300;
        Arrange();
    }

    public OptionCheck(string caption, string sub) : this()
    {
        Caption = caption;
        SubText = sub;
    }

    /// <summary>The checkbox itself, for wiring CheckedChanged.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CheckBox Box { get; }

    [Category("Appearance"), DefaultValue("")]
    public string Caption
    {
        get => Box.Text;
        set => Box.Text = value ?? "";
    }

    [Category("Appearance"), DefaultValue("")]
    [Editor("System.ComponentModel.Design.MultilineStringEditor, System.Design",
        "System.Drawing.Design.UITypeEditor, System.Drawing")]
    public string SubText
    {
        get => _sub.Text;
        set { _sub.Text = value ?? ""; Arrange(); }
    }

    [Category("Behavior"), DefaultValue(false)]
    public bool Checked
    {
        get => Box.Checked;
        set => Box.Checked = value;
    }

    private void Arrange()
    {
        var indent = Box.Height;
        // One line, measured from the font: reading the label's own height back out
        // lets any stretch of it feed into the option's height and stick there.
        var subH = _sub.Font.Height + 2;
        _sub.SetBounds(indent, Box.Height + 1, Math.Max(40, Width - indent), subH);
        var want = Box.Height + subH + 5;
        if (Height != want) Height = want;
    }

    /// <summary>Grey the whole option out and force it off, like the web tool's .disabled.</summary>
    public void SetAvailable(bool on)
    {
        Box.Enabled = on;
        Box.ForeColor = on ? Theme.Ink : Theme.InkFaint;
        _sub.ForeColor = on ? Theme.InkDim : Theme.InkFaint;
        if (!on) Box.Checked = false;
    }
}

/// <summary>
/// A small drawn cogwheel, used as the "edit this character" affordance next to a
/// player's name. Drawn rather than glyphed so it doesn't depend on a font having
/// the symbol, and so it can tint to show pending edits.
/// </summary>
public sealed class CogButton : Control
{
    private bool _hover;
    private bool _edited;

    /// <summary>Tint the cog when this character has unsaved appearance edits.</summary>
    [Category("Appearance"), DefaultValue(false)]
    public bool Edited
    {
        get => _edited;
        set { _edited = value; Invalidate(); }
    }

    public CogButton()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Panel;
        Cursor = Cursors.Hand;
        TabStop = false;
        Size = new Size(20, 20);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var color = !Enabled ? Theme.InkFaint
            : _edited ? Theme.Phosphor
            : _hover ? Theme.Ink
            : Theme.InkDim;

        float cx = Width / 2f, cy = Height / 2f;
        var r = Math.Min(Width, Height) / 2f - 2f;
        if (r < 3) return;

        using var pen = new Pen(color, Math.Max(1.4f, r * 0.26f));
        // hub
        var hub = r * 0.42f;
        g.DrawEllipse(pen, cx - hub, cy - hub, hub * 2, hub * 2);
        // teeth
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            g.DrawLine(pen, cx + dx * r * 0.68f, cy + dy * r * 0.68f, cx + dx * r, cy + dy * r);
        }
    }
}
