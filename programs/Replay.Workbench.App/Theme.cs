using ReplayWorkbench.Core;

namespace ReplayWorkbench.App;

/// <summary>
/// The workbench palette, carried over from docs/index.html: instrument panel -
/// dark slate, phosphor-cyan readouts, amber for the recorder/local player,
/// monospace data with one humanist sans for chrome.
/// </summary>
public static class Theme
{
    public static readonly Color Bg = FromHex("#0d1117");
    public static readonly Color Panel = FromHex("#141b24");
    public static readonly Color Panel2 = FromHex("#1b2531");
    public static readonly Color Line = FromHex("#263344");
    public static readonly Color Ink = FromHex("#d6e2f0");
    public static readonly Color InkDim = FromHex("#7d8da0");
    public static readonly Color InkFaint = FromHex("#4d5a6b");
    public static readonly Color Phosphor = FromHex("#39d4c8");
    public static readonly Color PhosphorDeep = FromHex("#1c6f6a");
    public static readonly Color Amber = FromHex("#f2b84c");
    public static readonly Color Violet = FromHex("#8b7cf6");
    public static readonly Color Danger = FromHex("#e8654f");

    public static readonly Font Sans = new("Segoe UI", 9f);
    public static readonly Font SansBold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9f);
    public static readonly Font MonoSmall = new("Consolas", 8f);
    public static readonly Font MonoBold = new("Consolas", 9.5f, FontStyle.Bold);
    public static readonly Font Title = new("Consolas", 13f, FontStyle.Bold);

    public static Color Tone(ReadoutTone tone) => tone switch
    {
        ReadoutTone.Cyan => Phosphor,
        ReadoutTone.Amber => Amber,
        _ => Ink,
    };

    private static Color FromHex(string hex) => ColorTranslator.FromHtml(hex);

    /// <summary>Paint a panel's border + rounded-ish frame the way the web cards look.</summary>
    public static void DrawFrame(Graphics g, Rectangle r, Color? border = null)
    {
        using var pen = new Pen(border ?? Line);
        g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }
}
