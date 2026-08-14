using System.Text;
using System.Text.RegularExpressions;

namespace ReplayWorkbench.Updater;

/// <summary>
/// The patch chain lives as a list literal inside two Python files
/// (<c>VERSION_CHAIN</c> in build_patchdiffs.py, <c>FALLBACK_CHAIN</c> in
/// bump_replay.py). Those files stay the authority, so this reads and extends
/// them in place rather than moving the data somewhere C#-friendly — the Python
/// tools remain runnable and remain the oracle this port is checked against.
/// </summary>
public static class ChainFile
{
    /// <summary>CRLF throughout, matching .gitattributes (* text=auto eol=crlf).</summary>
    public static void WriteText(string path, string text) =>
        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));

    public static string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8);

    private static (int Start, int End) Bounds(string text, string name)
    {
        var m = Regex.Match(text, $@"^{Regex.Escape(name)}\s*=\s*\[", RegexOptions.Multiline);
        if (!m.Success) throw new FatalException($"no {name} list found");
        var end = text.IndexOf(']', m.Index + m.Length);
        if (end < 0) throw new FatalException($"{name}: list literal is unterminated");
        return (m.Index + m.Length, end);
    }

    public static List<string> Read(string path, string name)
    {
        var text = ReadText(path);
        var (start, end) = Bounds(text, name);
        return Regex.Matches(text[start..end], "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>
    /// Append versions to a chain literal, keeping its hand-made line grouping.
    ///
    /// <para>The lists are grouped by minor version on purpose (one line per 7.x
    /// family), so this adds to the last line and wraps only when it would run
    /// long, instead of reflowing the whole block.</para>
    /// </summary>
    public static void Extend(string path, string name, IReadOnlyList<string> additions)
    {
        if (additions.Count == 0) return;
        var text = ReadText(path);
        var (start, end) = Bounds(text, name);
        // Work in \n space: the file is read with its CRLFs intact, and WriteText
        // puts them back, so normalise here or the line arithmetic sees stray \r.
        var body = text[start..end].Replace("\r\n", "\n");
        var lines = body.Split('\n').ToList();

        // Last line holding an entry; anything after it is the closing bracket's line.
        var idx = -1;
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Contains('"')) idx = i;
        if (idx < 0) throw new FatalException($"{name}: no entries to append to");

        var indent = Regex.Match(lines[idx], @"^\s*").Value;
        var row = lines[idx].TrimEnd();
        if (!row.EndsWith(',')) row += ",";

        foreach (var version in additions)
        {
            var piece = $" \"{version}\",";
            if (row.Length + piece.Length > 92)
            {
                lines[idx] = row;
                idx++;
                row = $"{indent}\"{version}\",";
                lines.Insert(idx, row);
            }
            else row += piece;
        }
        lines[idx] = row;

        WriteText(path, text[..start] + string.Join("\n", lines) + text[end..]);
    }
}
