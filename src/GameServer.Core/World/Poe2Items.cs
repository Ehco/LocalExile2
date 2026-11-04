using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.World;

/// <summary>
/// PoE2 item types and monster drops (PORTING-POE2.md, wave 25), from data/poe2/item-types.json
/// (scripts/re/poe2/item_types.py). PoE2's item data is PoE1's (<see cref="Item.Payload"/>): u32 HASH32, u16 flags, then
/// each component's data; currency is Base, Usable (no data) and Stack (u16).
/// </summary>
public static class Poe2Items
{
    /// <summary>A BaseItemTypes row: its HASH32, grid size, ItemVisualIdentity HASH16, .it components and stack size.</summary>
    public sealed record ItemType(string Id, int Row, string Name, uint Hash, byte Width, byte Height, int DropLevel,
        ushort VisualIdentity, string[] Components, int MaxStack = 1);

    public const string DefaultPath = "data/poe2/item-types.json";

    /// <summary>The item types by Id, or an empty table without the file.</summary>
    public static IReadOnlyDictionary<string, ItemType> Load(string path = DefaultPath)
    {
        if (!File.Exists(path)) return new Dictionary<string, ItemType>();
        var rows = JsonSerializer.Deserialize<ItemType[]>(File.ReadAllText(path), JsonOptions) ?? [];
        return rows.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>An item of the type, a stack of <paramref name="stack"/> (clamped to the type's stack size).</summary>
    public static Item Create(ItemType type, int stack = 1) =>
        new(type.Hash, type.Components, type.VisualIdentity) { StackSize = (ushort)Math.Clamp(stack, 1, Math.Max(1, type.MaxStack)) };

    public const int DefaultDropChance = 35;

    static int _dropChance = DefaultDropChance;

    /// <summary>The chance (percent) that a normal monster drops an item: 35 by default, our own number, not PoE2's. The
    /// Gateway sets it from Game_POE2_DROP_CHANCE.</summary>
    public static int DropChance
    {
        get => Volatile.Read(ref _dropChance);
        set => Volatile.Write(ref _dropChance, Math.Clamp(value, 0, 100));
    }

    /// <summary>What a normal monster drops, by weight: the Act 1 currency. Our own table, not PoE2's.</summary>
    public static readonly (string Id, int Weight)[] Drops =
    [
        ("Metadata/Items/Currency/CurrencyIdentification", 60),     // Scroll of Wisdom
        ("Metadata/Items/Currency/CurrencyUpgradeToMagic", 25),     // Orb of Transmutation
        ("Metadata/Items/Currency/CurrencyAddModToMagic", 15),      // Orb of Augmentation
    ];

    /// <summary>A kill's drop, or null: one item of <see cref="Drops"/> whose drop level the monster reaches, with
    /// <see cref="DropChance"/>.</summary>
    public static Item? Roll(IReadOnlyDictionary<string, ItemType> types, int monsterLevel, Random random)
    {
        var table = Drops.Where(d => types.TryGetValue(d.Id, out var t) && t.DropLevel <= monsterLevel).ToList();
        if (table.Count == 0 || random.Next(100) >= DropChance) return null;
        int pick = random.Next(table.Sum(d => d.Weight));
        foreach (var (id, weight) in table)
        {
            if (pick < weight) return Create(types[id]);
            pick -= weight;
        }
        return null;
    }
}
