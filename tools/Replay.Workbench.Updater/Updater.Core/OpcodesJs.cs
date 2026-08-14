using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReplayWorkbench.Updater;

/// <summary>
/// Surgery on docs/opcodes.js. The file is hand-maintained around the generated
/// bits, so every edit here is deliberately local — find the one literal, rewrite
/// it, leave the rest of the file exactly as it was.
/// </summary>
public static class OpcodesJs
{
    /// <summary>Span of the object/array literal assigned by <paramref name="decl"/> (e.g. "const FOO").</summary>
    public static (int Start, int End) LiteralBounds(string text, string decl)
    {
        var i = text.IndexOf(decl, StringComparison.Ordinal);
        if (i < 0) throw new FatalException($"{decl}: not found");
        var j = text.IndexOf('=', i) + 1;
        while (j < text.Length && (text[j] is ' ' or '\t' or '\r' or '\n')) j++;
        if (j >= text.Length) throw new FatalException($"{decl}: no literal after '='");

        var open = text[j];
        var close = open switch { '[' => ']', '{' => '}', _ => throw new FatalException($"{decl}: not a literal") };
        int depth = 0, k = j;
        var inStr = false;
        while (k < text.Length)
        {
            var c = text[k];
            if (inStr)
            {
                if (c == '\\') { k += 2; continue; }
                if (c == '"') inStr = false;
            }
            else if (c == '"') inStr = true;
            else if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return (j, k + 1);
            }
            k++;
        }
        throw new FatalException($"{decl}: literal is unterminated");
    }

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true };

    /// <summary>Every OPCODE_TABLES entry, in file order, each keeping its own name order.</summary>
    public static List<(string Patch, OpcodeTable Table)> ReadTables(string text)
    {
        var (start, end) = LiteralBounds(text, "const OPCODE_TABLES");
        using var doc = JsonDocument.Parse(text[start..end], Lenient);
        var outp = new List<(string, OpcodeTable)>();
        foreach (var patch in doc.RootElement.EnumerateObject())
        {
            var table = new OpcodeTable();
            foreach (var entry in patch.Value.EnumerateObject()) table[entry.Name] = entry.Value.GetInt32();
            outp.Add((patch.Name, table));
        }
        return outp;
    }

    /// <summary>Add one OPCODE_TABLES entry just before the literal's closing brace.</summary>
    public static string InsertTable(string text, string patch, OpcodeTable table)
    {
        var (start, end) = LiteralBounds(text, "const OPCODE_TABLES");
        var body = text[start..end];
        var entries = Regex.Matches(body, "^([ \t]*)\"[^\"]+\":", RegexOptions.Multiline);
        var indent = entries.Count > 0 ? entries[^1].Groups[1].Value : "\t";
        var line = $"{indent}\"{patch}\": {table.ToCompactJson()},\n";

        var close = start + body.LastIndexOf('}');
        var lineStart = text.LastIndexOf('\n', close - 1, close - start) + 1;
        return text[..lineStart] + line + text[lineStart..];
    }

    /// <summary>Add or replace one build → patch pair, rewriting the literal in build order.</summary>
    public static string SetBuildToPatch(string text, int build, string patch)
    {
        var (start, end) = LiteralBounds(text, "const BUILD_TO_PATCH");
        var pairs = new SortedDictionary<int, string>();
        foreach (Match m in Regex.Matches(text[start..end], "(\\d+)\\s*:\\s*\"([^\"]+)\""))
            pairs[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
        pairs[build] = patch;
        var body = "{ " + string.Join(", ", pairs.Select(kv => $"{kv.Key}: \"{kv.Value}\"")) + " }";
        return text[..start] + body + text[end..];
    }

    public static string SetLatest(string text, string patch, int build)
    {
        var n1 = 0;
        var n2 = 0;
        text = Regex.Replace(text, "^(\\s*(?:let|const)\\s+LATEST_PATCH\\s*=\\s*\")[^\"]*(\")",
            m => { n1++; return m.Groups[1].Value + patch + m.Groups[2].Value; },
            RegexOptions.Multiline);
        text = Regex.Replace(text, "^(\\s*(?:let|const)\\s+LATEST_GAME_BUILD\\s*=\\s*)\\d+",
            m => { n2++; return m.Groups[1].Value + build; },
            RegexOptions.Multiline);
        if (n1 == 0 || n2 == 0)
            throw new FatalException("could not find LATEST_PATCH / LATEST_GAME_BUILD to update");
        return text;
    }

    public static string ReadLatest(string text)
    {
        var m = Regex.Match(text, "^\\s*(?:let|const)\\s+LATEST_PATCH\\s*=\\s*\"([^\"]*)\"", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : "?";
    }

    public static int ReadLatestBuild(string text)
    {
        var m = Regex.Match(text, "^\\s*(?:let|const)\\s+LATEST_GAME_BUILD\\s*=\\s*(\\d+)", RegexOptions.Multiline);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}
