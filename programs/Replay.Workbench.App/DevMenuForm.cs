using System.Text.Json;
using ReplayWorkbench.Core;

namespace ReplayWorkbench.App;

/// <summary>
/// Register an opcode table + build number at runtime, so a new game patch can be
/// tested before it is baked into the data files.  Applying promotes the table to
/// latest: transpose targets it and the build re-stamp uses it.  Nothing is
/// persisted - it lives for the life of the process.
/// </summary>
/// <remarks>
/// The whole layout lives in DevMenuForm.Designer.cs and is editable in the
/// WinForms designer; this file is behaviour only.
/// </remarks>
internal sealed partial class DevMenuForm : Form
{
    public int RegisteredBuild { get; private set; }
    public int RegisteredCount { get; private set; }

    /// <summary>Kept so an error message can be replaced by the original blurb.</summary>
    private readonly string _defaultHint;

    public DevMenuForm()
    {
        InitializeComponent();
        _defaultHint = hintLabel.Text;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private void OnPrefillClick(object? sender, EventArgs e)
    {
        buildBox.Text = OpcodeData.LatestGameBuild.ToString();
        var table = OpcodeData.RawTable(OpcodeData.LatestPatch);
        jsonBox.Text = table is null ? "" : JsonSerializer.Serialize(table);
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void OnApplyClick(object? sender, EventArgs e) => Apply();

    private void Hint(string message, bool error)
    {
        hintLabel.Text = message;
        hintLabel.ForeColor = error ? Theme.Danger : Theme.InkDim;
    }

    private void Apply()
    {
        if (!int.TryParse(buildBox.Text.Trim(), out var build) || build <= 0)
        {
            Hint("Enter a valid positive integer build number.", true);
            return;
        }

        Dictionary<string, int>? table;
        try
        {
            table = NormalizeTable(jsonBox.Text);
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

        Hint(_defaultHint, false);
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
