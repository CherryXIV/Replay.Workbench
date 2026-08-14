using System.Text.Json;
using ReplayWorkbench.Core;

namespace ReplayWorkbench.App;

/// <summary>
/// Register an opcode table + build number at runtime, so a new game patch can be
/// tested before it is baked into the data files.  Applying promotes the table to
/// latest: transpose targets it and the build re-stamp uses it.  Nothing is
/// persisted - it lives for the life of the process.
/// </summary>
internal sealed class DevMenuForm : Form
{
    private readonly TextBox _build = new();
    private readonly TextBox _json = new();
    private readonly Label _hint = new();

    public int RegisteredBuild { get; private set; }
    public int RegisteredCount { get; private set; }

    private const string DefaultHint =
        "Registers this opcode table for the build, then re-parses the loaded file. " +
        "Plain {name:opcode} maps and a full FFXIVOpcodes opcodes.json are both accepted.";

    public DevMenuForm()
    {
        Text = "Register opcodes";
        BackColor = Theme.Panel;
        ForeColor = Theme.Ink;
        Font = Theme.Sans;
        ClientSize = new Size(620, 470);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;

        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16), BackColor = Theme.Panel };

        var buildLbl = Caption("Game build number", 0);
        _build.SetBounds(0, 22, 200, 24);
        Style(_build);

        var jsonLbl = Caption("Opcodes JSON", 56);
        _json.SetBounds(0, 78, 588, 280);
        _json.Multiline = true;
        _json.ScrollBars = ScrollBars.Vertical;
        _json.WordWrap = false;
        _json.PlaceholderText = "{ \"ActorCast\":457, \"ActorControl\":415, \"NpcSpawn\":888, … }";
        Style(_json);

        _hint.SetBounds(0, 366, 588, 48);
        _hint.Font = Theme.MonoSmall;
        _hint.ForeColor = Theme.InkDim;
        _hint.Text = DefaultHint;

        var prefill = new FlatButton("Prefill latest") { Width = 130, Location = new Point(0, 402) };
        prefill.Click += (_, _) =>
        {
            _build.Text = OpcodeData.LatestGameBuild.ToString();
            var table = OpcodeData.RawTable(OpcodeData.LatestPatch);
            _json.Text = table is null ? "" : JsonSerializer.Serialize(table);
        };

        var cancel = new FlatButton("Cancel") { Width = 100, Location = new Point(378, 402) };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var apply = new FlatButton("Apply") { Accent = true, Width = 100, Location = new Point(488, 402) };
        apply.Click += (_, _) => Apply();

        pad.Controls.AddRange(new Control[] { buildLbl, _build, jsonLbl, _json, _hint, prefill, cancel, apply });
        Controls.Add(pad);

        CancelButton = cancel;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        return;

        static Label Caption(string text, int y) => new()
        {
            Text = text, Font = Theme.SansBold, ForeColor = Theme.Ink,
            AutoSize = true, Location = new Point(0, y),
        };
    }

    private static void Style(TextBox t)
    {
        t.BackColor = Theme.Panel2;
        t.ForeColor = Theme.Ink;
        t.Font = Theme.Mono;
        t.BorderStyle = BorderStyle.FixedSingle;
    }

    private void Hint(string message, bool error)
    {
        _hint.Text = message;
        _hint.ForeColor = error ? Theme.Danger : Theme.InkDim;
    }

    private void Apply()
    {
        if (!int.TryParse(_build.Text.Trim(), out var build) || build <= 0)
        {
            Hint("Enter a valid positive integer build number.", true);
            return;
        }

        Dictionary<string, int>? table;
        try
        {
            table = NormalizeTable(_json.Text);
        }
        catch (JsonException ex)
        {
            Hint("Opcodes JSON didn't parse: " + ex.Message, true);
            return;
        }
        if (table is null)
        {
            Hint("Couldn't read an opcode table from that JSON (expected {name:opcode} or a FFXIVOpcodes opcodes.json).", true);
            return;
        }

        try
        {
            // Reject a self-contradicting table here, at the door: registering it
            // promotes it to the transpose target, and every export made against it
            // would crash the game.
            OpcodeData.RegisterTable(build, table);
        }
        catch (Exception ex)
        {
            Hint("Rejected: " + ex.Message, true);
            return;
        }

        RegisteredBuild = build;
        RegisteredCount = table.Count;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// Accept either a plain {name: opcode} map or a full FFXIVOpcodes
    /// opcodes.json (a region list whose lists[].ServerZoneIpcType holds
    /// {name, opcode} pairs).
    /// </summary>
    internal static Dictionary<string, int>? NormalizeTable(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            var flat = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in root.EnumerateObject())
                if (e.Value.ValueKind == JsonValueKind.Number && e.Value.TryGetInt32(out var v))
                    flat[e.Name] = v;
            if (flat.Count > 0) return flat;
        }

        // FFXIVOpcodes shape: [{ region, lists: { ServerZoneIpcType: [{name, opcode}] } }]
        var candidates = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : new List<JsonElement> { root };

        foreach (var entry in candidates)
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("lists", out var lists)) continue;
            if (!lists.TryGetProperty("ServerZoneIpcType", out var szc)) continue;
            if (szc.ValueKind != JsonValueKind.Array) continue;
            var table = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in szc.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                if (!item.TryGetProperty("opcode", out var o) || !o.TryGetInt32(out var op)) continue;
                table[n.GetString()!] = op;
            }
            if (table.Count > 0) return table;
        }
        return null;
    }
}
