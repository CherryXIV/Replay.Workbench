using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace ReplayWorkbench.Updater;

/// <summary>
/// Lifts an object/array literal out of one of the docs/*.js data files and hands
/// it back as JSON. Those files are generator output, so the only things standing
/// between them and JSON are comments, bare keys and trailing commas.
/// </summary>
public static class JsLiteral
{
    /// <summary>Remove // and /* */ comments, but only outside string literals.</summary>
    public static string StripComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];
            if (c == '"')
            {
                var j = i + 1;
                while (j < src.Length && src[j] != '"') j += src[j] == '\\' ? 2 : 1;
                sb.Append(src, i, Math.Min(j + 1, src.Length) - i);
                i = j + 1;
            }
            else if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                var nl = src.IndexOf('\n', i);
                if (nl < 0) break;
                i = nl;
            }
            else if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                var close = src.IndexOf("*/", i, StringComparison.Ordinal);
                if (close < 0) break;
                i = close + 2;
            }
            else { sb.Append(c); i++; }
        }
        return sb.ToString();
    }

    /// <summary>The balanced {...} or [...] literal assigned to <paramref name="name"/>.</summary>
    public static string Extract(string src, string name)
    {
        var m = Regex.Match(src, @"(?:const|let|var)\s+" + Regex.Escape(name) + @"\s*=\s*");
        if (!m.Success) throw new FatalException($"{name} not found");
        var i = m.Index + m.Length;
        if (i >= src.Length || (src[i] != '{' && src[i] != '[')) throw new FatalException($"{name} is not a literal");
        var close = src[i] == '{' ? '}' : ']';
        int depth = 0, j = i;
        var inStr = false;
        while (j < src.Length)
        {
            var c = src[j];
            if (inStr)
            {
                if (c == '\\') { j += 2; continue; }
                if (c == '"') inStr = false;
            }
            else if (c == '"') inStr = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']')
            {
                depth--;
                if (depth == 0)
                {
                    if (c != close) throw new FatalException($"{name}: mismatched brackets");
                    return src[i..(j + 1)];
                }
            }
            j++;
        }
        throw new FatalException($"{name}: unterminated literal");
    }

    /// <summary>Parse a JS object literal: quote bare identifier / numeric keys, then read it as JSON.</summary>
    public static JsonDocument Parse(string literal)
    {
        var json = Regex.Replace(literal, @"([{,]\s*)([A-Za-z_$][\w$]*|\d+)\s*:", "$1\"$2\":");
        return JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
    }

    /// <summary>Read a scalar assignment such as <c>let LATEST_GAME_BUILD = 13820768;</c>.</summary>
    public static string Scalar(string src, string name)
    {
        var m = Regex.Match(src, @"(?:const|let|var)\s+" + Regex.Escape(name) + @"\s*=\s*([^;]+);");
        if (!m.Success) throw new FatalException($"{name} not found");
        return m.Groups[1].Value.Trim();
    }
}
