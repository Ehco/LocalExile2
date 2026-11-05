using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The PoE2 passive skill tree's nodes (PORTING-POE2.md, wave 28), from data/poe2/passives.json
/// (scripts/re/poe2/passives.py): by PassiveSkillGraphId, the id the client allocates (C→S 0x3c) and the server lists
/// (S→C 0x3a), with the stats each grants. An attribute node ("Attribute", +5 to an attribute of the player's choice)
/// carries no stat of its own: the client sends the chosen generic node (generic_attribute_strength ...) with the
/// allocation, and that node's stat counts instead. With it, what a refund costs in gold at each level
/// (data/poe2/gold-respec-prices.json, GoldRespecPrices.datc64).
/// </summary>
public sealed class Poe2Passives
{
    public sealed record Node(ushort GraphId, string Id, string Name, IReadOnlyList<(string Id, uint Key, int Value)> Stats,
        int Points = 0, bool Attribute = false, bool Keystone = false, bool Notable = false);

    public const string DefaultPath = "data/poe2/passives.json", DefaultPricesPath = "data/poe2/gold-respec-prices.json";

    readonly IReadOnlyDictionary<ushort, Node> _nodes;
    readonly IReadOnlyList<int> _respecPrices;

    /// <param name="respecPrices">The gold a refund of one node costs, by level from 1.</param>
    public Poe2Passives(IReadOnlyDictionary<ushort, Node> nodes, IReadOnlyList<int>? respecPrices = null) =>
        (_nodes, _respecPrices) = (nodes, respecPrices ?? []);

    public static readonly Poe2Passives Empty = new(new Dictionary<ushort, Node>());

    public int Count => _nodes.Count;

    public Node? Find(ushort graphId) => _nodes.GetValueOrDefault(graphId);

    /// <summary>The gold a refund of one node costs at <paramref name="level"/> (clamped to the table); 0 without the
    /// table, so refunds are free.</summary>
    public int RefundCost(int level) => _respecPrices.Count == 0 ? 0 : _respecPrices[Math.Clamp(level, 1, _respecPrices.Count) - 1];

    /// <summary>The tables of <paramref name="path"/> and <paramref name="pricesPath"/>; a missing file leaves its
    /// table empty (<see cref="Empty"/> without both).</summary>
    public static Poe2Passives Load(string path = DefaultPath, string pricesPath = DefaultPricesPath)
    {
        var prices = LoadPrices(pricesPath);
        if (!File.Exists(path)) return prices.Count == 0 ? Empty : new(new Dictionary<ushort, Node>(), prices);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var nodes = new Dictionary<ushort, Node>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            ushort id = (ushort)e.GetProperty("graphId").GetUInt32();
            var stats = e.TryGetProperty("stats", out var list)
                ? list.EnumerateArray().Select(s => (s[0].GetString() ?? "", s[1].GetUInt32(), s[2].GetInt32())).ToList() : [];
            nodes.TryAdd(id, new Node(id, e.GetProperty("id").GetString() ?? "", e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                stats, e.TryGetProperty("points", out var p) ? p.GetInt32() : 0, Flag(e, "attribute"), Flag(e, "keystone"), Flag(e, "notable")));
        }
        return new(nodes, prices);
    }

    static IReadOnlyList<int> LoadPrices(string path)
    {
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return [.. doc.RootElement.EnumerateArray().OrderBy(e => e[0].GetInt32()).Select(e => e[1].GetInt32())];
    }

    static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>An allocation as the character record and the wire keep it: the node in the low 16 bits, the chosen
    /// attribute node (0 for none) in the high 16.</summary>
    public static int Encode(ushort node, ushort choice) => node | choice << 16;

    public static (ushort Node, ushort Choice) Decode(int allocation) => ((ushort)allocation, (ushort)(allocation >> 16));

    /// <summary>The stats these allocations grant: each node's, or for a node allocated with a choice the chosen
    /// node's. Unknown nodes grant nothing.</summary>
    public IEnumerable<(string Id, uint Key, int Value)> Modifiers(IEnumerable<(ushort Node, ushort Choice)> allocations)
    {
        foreach (var (node, choice) in allocations)
        {
            var n = choice != 0 && Find(choice) is { } chosen ? chosen : Find(node);
            if (n is null) continue;
            foreach (var s in n.Stats) yield return s;
        }
    }
}
