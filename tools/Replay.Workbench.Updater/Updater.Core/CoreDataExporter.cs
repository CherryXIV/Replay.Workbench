using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ReplayWorkbench.Updater;

/// <summary>
/// Exports the browser tool's data files as the JSON the desktop core embeds.
///
/// <para>docs/opcodes.js, docs/patchdiffs.js and docs/afgear.js stay the single
/// source of truth — they are what the web workbench loads and what the patch
/// update writes. This lifts them into programs/Replay.Workbench.Core/Data/,
/// which the library embeds as resources.</para>
///
/// <para>Port of tools/export_core_data.py, and formatted to match it byte for
/// byte (Python's <c>json.dumps(indent=1)</c>) so the two can be diffed.</para>
/// </summary>
public static class CoreDataExporter
{
    public sealed record Written(string Path, int Bytes);

    public static List<Written> Export(string repoRoot, Action<string> log)
    {
        var docs = Path.Combine(repoRoot, "docs");
        var outDir = Path.Combine(repoRoot, "programs", "Replay.Workbench.Core", "Data");
        Directory.CreateDirectory(outDir);

        var opcodes = JsLiteral.StripComments(File.ReadAllText(Path.Combine(docs, "opcodes.js"), Encoding.UTF8));
        var diffs = JsLiteral.StripComments(File.ReadAllText(Path.Combine(docs, "patchdiffs.js"), Encoding.UTF8));
        var afgear = JsLiteral.StripComments(File.ReadAllText(Path.Combine(docs, "afgear.js"), Encoding.UTF8));

        var latestPatch = JsonSerializer.Deserialize<string>(JsLiteral.Scalar(opcodes, "LATEST_PATCH"))!;
        var latestBuild = int.Parse(JsLiteral.Scalar(opcodes, "LATEST_GAME_BUILD"));

        var written = new List<Written>();
        log("exporting core data:");

        using (var tables = JsLiteral.Parse(JsLiteral.Extract(opcodes, "OPCODE_TABLES")))
        using (var buildToPatch = JsLiteral.Parse(JsLiteral.Extract(opcodes, "BUILD_TO_PATCH")))
            written.Add(Write(Path.Combine(outDir, "opcodes.json"), log, repoRoot, w =>
            {
                w.WriteStartObject();
                w.WriteString("latestPatch", latestPatch);
                w.WriteNumber("latestGameBuild", latestBuild);
                w.WritePropertyName("buildToPatch");
                buildToPatch.RootElement.WriteTo(w);
                w.WritePropertyName("opcodeTables");
                tables.RootElement.WriteTo(w);
                w.WriteEndObject();
            }));

        using (var chain = JsLiteral.Parse(JsLiteral.Extract(diffs, "PATCH_CHAIN")))
        using (var patchDiffs = JsLiteral.Parse(JsLiteral.Extract(diffs, "PATCH_DIFFS")))
            written.Add(Write(Path.Combine(outDir, "patchdiffs.json"), log, repoRoot, w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("patchChain");
                chain.RootElement.WriteTo(w);
                w.WritePropertyName("patchDiffs");
                patchDiffs.RootElement.WriteTo(w);
                w.WriteEndObject();
            }));

        using (var gear = JsLiteral.Parse(JsLiteral.Extract(afgear, "JOB_AF_GEAR")))
            written.Add(Write(Path.Combine(outDir, "afgear.json"), log, repoRoot,
                w => gear.RootElement.WriteTo(w)));

        return written;
    }

    private static Written Write(string path, Action<string> log, string repoRoot, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        // indent 1 space, matching Python's json.dumps(indent=1); the Python is the
        // oracle this port is diffed against, so the whitespace has to agree too.
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = true,
                   IndentCharacter = ' ',
                   IndentSize = 1,
                   // match Python's json.dumps, which leaves > < & ' + alone
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
            body(w);

        // Utf8JsonWriter breaks lines with Environment.NewLine, so normalise to \n
        // first: converting blind would turn an existing CRLF into CR + CRLF.
        var text = Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n";
        ChainFile.WriteText(path, text);
        log($"  {Path.GetRelativePath(repoRoot, path)}  ({text.Length:N0} bytes)");
        return new Written(path, text.Length);
    }
}
