namespace ReplayWorkbench.Updater;

/// <summary>
/// An IPC name → opcode table that keeps its order.
///
/// <para>Order matters: a derived table is written back into docs/opcodes.js as a
/// compact JSON object, and the Python it is checked against emits names in the
/// order it carried them forward from the source table. A plain dictionary would
/// reproduce the right values in the wrong order and fail the comparison for no
/// real reason.</para>
/// </summary>
public sealed class OpcodeTable
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    public int Count => _order.Count;
    public IReadOnlyList<string> Names => _order;

    public int this[string name]
    {
        get => _byName[name];
        set
        {
            if (!_byName.ContainsKey(name)) _order.Add(name);
            _byName[name] = value;
        }
    }

    public bool TryGet(string name, out int opcode) => _byName.TryGetValue(name, out opcode);
    public bool Contains(string name) => _byName.ContainsKey(name);
    public IEnumerable<(string Name, int Opcode)> Entries => _order.Select(n => (n, _byName[n]));
    public IEnumerable<int> Opcodes => _order.Select(n => _byName[n]);

    /// <summary>Compact JSON, matching Python's <c>json.dumps(separators=(",", ":"))</c>.</summary>
    public string ToCompactJson() =>
        "{" + string.Join(",", Entries.Select(e => $"\"{e.Name}\":{e.Opcode}")) + "}";

    /// <summary>Opcodes carrying more than one name — the shape transpose refuses.</summary>
    public Dictionary<int, List<string>> Collisions()
    {
        var byOp = new Dictionary<int, List<string>>();
        foreach (var (name, op) in Entries)
        {
            if (!byOp.TryGetValue(op, out var l)) byOp[op] = l = new List<string>();
            l.Add(name);
        }
        return byOp.Where(kv => kv.Value.Count > 1).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
