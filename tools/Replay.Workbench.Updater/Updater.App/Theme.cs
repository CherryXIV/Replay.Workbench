using System.ComponentModel;

namespace ReplayWorkbench.Updater.App;

/// <summary>
/// The workbench palette again. Deliberately a small copy rather than a reference
/// to the editor's Theme: this app rebuilds that solution, and must not hold an
/// assembly MSBuild is about to overwrite.
/// </summary>
internal static class Theme
{
    public static readonly Color Bg = ColorTranslator.FromHtml("#0d1117");
    public static readonly Color Panel = ColorTranslator.FromHtml("#141b24");
    public static readonly Color Panel2 = ColorTranslator.FromHtml("#1b2531");
    public static readonly Color Line = ColorTranslator.FromHtml("#263344");
    public static readonly Color Ink = ColorTranslator.FromHtml("#d6e2f0");
    public static readonly Color InkDim = ColorTranslator.FromHtml("#7d8da0");
    public static readonly Color InkFaint = ColorTranslator.FromHtml("#4d5a6b");
    public static readonly Color Phosphor = ColorTranslator.FromHtml("#39d4c8");
    public static readonly Color PhosphorDeep = ColorTranslator.FromHtml("#1c6f6a");
    public static readonly Color Amber = ColorTranslator.FromHtml("#f2b84c");
    public static readonly Color Danger = ColorTranslator.FromHtml("#e8654f");

    public static readonly Font Sans = new("Segoe UI", 9f);
    public static readonly Font SansBold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9f);
    public static readonly Font MonoSmall = new("Consolas", 8f);

    public static void Frame(Graphics g, Rectangle r)
    {
        using var pen = new Pen(Line);
        g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }
}

/// <summary>Flat dark button; <see cref="Accent"/> marks the primary action.</summary>
internal sealed class FlatButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Accent { get; init; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Danger { get; init; }

    public FlatButton(string text)
    {
        Text = text;
        FlatStyle = FlatStyle.Flat;
        Font = Theme.Sans;
        AutoSize = false;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 10, 0);
        FitText();
    }

    private void FitText()
    {
        var s = TextRenderer.MeasureText(Text, Font);
        Size = new Size(s.Width + 30, s.Height + 16);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitText(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitText(); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FitText();
        var fore = Danger ? Theme.Danger : Accent ? Theme.Phosphor : Theme.Ink;
        FlatAppearance.BorderColor = Accent ? Theme.PhosphorDeep : Theme.Line;
        FlatAppearance.MouseOverBackColor = Accent ? Theme.PhosphorDeep : Theme.Panel2;
        FlatAppearance.MouseDownBackColor = Theme.Panel2;
        BackColor = Accent ? Color.FromArgb(24, 52, 56) : Theme.Panel;
        ForeColor = fore;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ForeColor = Enabled ? (Danger ? Theme.Danger : Accent ? Theme.Phosphor : Theme.Ink) : Theme.InkFaint;
    }
}
