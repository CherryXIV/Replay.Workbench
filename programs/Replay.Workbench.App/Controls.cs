using System.ComponentModel;
using System.Drawing.Drawing2D;
using ReplayWorkbench.Core;

namespace ReplayWorkbench.App;

/// <summary>
/// A titled panel: header strip with a name on the left and a meta note on the
/// right, wrapping one content control.  Its height tracks the content's, so the
/// form can stack cards without relying on nested auto-sizing.
/// </summary>
internal sealed class CardPanel : Panel
{
    public const int HeadHeight = 30;
    private const int BottomPad = 1;

    private readonly string _title;
    private string _meta = "";
    private Control? _content;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Meta
    {
        get => _meta;
        set { _meta = value; Invalidate(new Rectangle(0, 0, Width, HeadHeight)); }
    }

    /// <summary>Raised when the card resized itself around new content.</summary>
    public event EventHandler? HeightChanged;

    public CardPanel(string title)
    {
        _title = title;
        BackColor = Theme.Panel;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Height = HeadHeight + BottomPad;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control? Content
    {
        get => _content;
        set
        {
            if (_content is not null)
            {
                _content.SizeChanged -= OnContentSized;
                Controls.Remove(_content);
            }
            _content = value;
            if (value is null) return;
            value.Location = new Point(1, HeadHeight);
            value.SizeChanged += OnContentSized;
            Controls.Add(value);
            FitContent();
        }
    }

    private void OnContentSized(object? sender, EventArgs e) => FitContent();

    public void FitContent()
    {
        if (_content is null) return;
        _content.Width = Math.Max(1, Width - 2);
        var want = HeadHeight + _content.Height + BottomPad;
        if (Height == want) return;
        Height = want;
        Parent?.Invalidate(Bounds, false);
        HeightChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitContent();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var b = new SolidBrush(Theme.Panel2))
            g.FillRectangle(b, 1, 1, Width - 2, HeadHeight - 1);
        using (var p = new Pen(Theme.Line))
        {
            g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            g.DrawLine(p, 1, HeadHeight - 1, Width - 2, HeadHeight - 1);
        }
        TextRenderer.DrawText(g, _title, Theme.SansBold, new Point(14, 8), Theme.Ink);
        if (_meta.Length > 0)
        {
            var size = TextRenderer.MeasureText(g, _meta, Theme.MonoSmall);
            TextRenderer.DrawText(g, _meta, Theme.MonoSmall,
                new Point(Width - 14 - size.Width, 9), Theme.InkDim);
        }
        base.OnPaint(e);
    }
}

/// <summary>
/// The recording header: a responsive grid of label-over-value cells, painted
/// rather than laid out so it reflows with the window without flicker.
/// </summary>
internal sealed class ReadoutView : Control
{
    private IReadOnlyList<(string Key, string Value, ReadoutTone Tone)> _cells = [];

    public ReadoutView()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Panel;
    }

    public void SetCells(IReadOnlyList<(string, string, ReadoutTone)> cells)
    {
        _cells = cells;
        Layout();
        Invalidate();
    }

    private const int MinCellW = 210;
    private const int Pad = 14;
    private const int Gap = 10;

    private int KeyH => Theme.MonoSmall.Height;
    private int ValueH => Theme.Mono.Height;
    private int CellH => KeyH + ValueH + 14;

    private int Columns => Math.Max(1, (Width - Pad * 2 + Gap) / (MinCellW + Gap));

    private new void Layout()
    {
        if (_cells.Count == 0) { Height = 0; return; }
        var rows = (int)Math.Ceiling(_cells.Count / (double)Columns);
        Height = Pad * 2 + rows * CellH;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Layout();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
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
internal sealed class TimelineControl : Control
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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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
internal sealed class DarkListView : ListView
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
internal sealed class FlatButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Accent { get; init; }

    public FlatButton(string text)
    {
        Text = text;
        FlatStyle = FlatStyle.Flat;
        Font = Theme.Sans;
        AutoSize = false;
        Margin = new Padding(0, 0, 12, 0);
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        FitText();
    }

    private void FitText()
    {
        var s = TextRenderer.MeasureText(Text, Font);
        Size = new Size(s.Width + 32, s.Height + 16);
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
        FlatAppearance.BorderColor = Accent ? Theme.PhosphorDeep : Theme.Line;
        FlatAppearance.MouseOverBackColor = Accent ? Theme.PhosphorDeep : Theme.Panel2;
        FlatAppearance.MouseDownBackColor = Theme.Panel2;
        BackColor = Accent ? Color.FromArgb(24, 52, 56) : Theme.Panel;
        ForeColor = Accent ? Theme.Phosphor : Theme.Ink;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ForeColor = Enabled ? (Accent ? Theme.Phosphor : Theme.Ink) : Theme.InkFaint;
    }
}

/// <summary>Checkbox with a bold caption and a dim explanatory line beneath it.</summary>
internal sealed class OptionCheck : Panel
{
    public CheckBox Box { get; }
    private readonly Label _sub;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SubText
    {
        get => _sub.Text;
        set => _sub.Text = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => Box.Checked;
        set => Box.Checked = value;
    }

    public OptionCheck(string caption, string sub)
    {
        BackColor = Theme.Panel;
        Box = new CheckBox
        {
            Text = caption,
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
            Text = sub,
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
        Arrange();
    }

    private void Arrange()
    {
        var indent = Box.Height;
        _sub.SetBounds(indent, Box.Height + 1, Math.Max(40, Width - indent), _sub.Height);
        var want = Box.Height + _sub.Height + 5;
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
internal sealed class CogButton : Control
{
    private bool _hover;
    private bool _edited;

    /// <summary>Tint the cog when this character has unsaved appearance edits.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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
