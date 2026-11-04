using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.World;

/// <summary>
/// PoE2 item types and monster drops (PORTING-POE2.md, wave 25), from data/poe2/item-types.json
/// (scripts/re/poe2/item_types.py). PoE2's item data is PoE1's (<see cref="Item.Payload"/>): u32 HASH32, u16 flags, then
/// each component's data; currency is Base, Usable (no data) and Stack (u16).
/// </summary>
public static class Poe2Items
{
    /// <summary>A BaseItemTypes row: its HASH32, grid size, ItemVisualIdentity HASH16, .it components and stack size.
    /// A class's starting weapon also has its ItemClasses row, ItemStances row, weapon values, the Characters rows that
    /// start with it, the basic attack it gives and its WieldableClasses row (wave 26).</summary>
    public sealed record ItemType(string Id, int Row, string Name, uint Hash, byte Width, byte Height, int DropLevel,
        ushort VisualIdentity, string[] Components, int MaxStack = 1, int? ItemClass = null, byte? Stance = null,
        WeaponValues? Weapon = null, int[]? StartingClasses = null, BasicAttack? Attack = null,
        int? Wieldable = null);

    /// <summary>WeaponTypes: damage, attack time, reach in cells and critical chance (hundredths of a percent).</summary>
    public sealed record WeaponValues(int MinimumDamage, int MaximumDamage, int AttackMs, int Range, int Critical);

    /// <summary>A weapon's basic attack (ActiveSkills player_melee_*): its GEPL and GESSPL rows at level 1.</summary>
    public sealed record BasicAttack(uint EffectsPerLevelRow, uint StatSetsPerLevelRow);

    /// <summary>The weapon a class starts with (Characters StartWeapons), or null.</summary>
    public static ItemType? StartingWeapon(IReadOnlyDictionary<string, ItemType> types, int classRow) =>
        types.Values.FirstOrDefault(t => t.StartingClasses?.Contains(classRow) == true);

    /// <summary>How the player's model shows a weapon in the main hand: visual slot 0, type ItemClasses row + 1.</summary>
    public static VisibleItem Visible(ItemType weapon) =>
        new(CharacterPreviewPoe2.Weapon1, weapon.VisualIdentity, weapon.Stance ?? 0xff, (byte)((weapon.ItemClass ?? -1) + 1));

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
        new(type.Hash, type.Components, type.VisualIdentity) { StackSize = (ushort)Math.Clamp(stack, 1, Math.Max(1, type.MaxStack)), Poe2 = true };

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

    /// <summary>PoE2's gold, a pile on the ground that the character's gold takes (BaseItemTypes row 1943).</summary>
    public const string GoldCoin = "Metadata/Items/Currency/GoldCoin";

    /// <summary>The chance (percent) that a normal monster drops a gold pile, and the pile's size: our own numbers, not
    /// PoE2's (wave 29).</summary>
    public const int GoldDropChance = 50, GoldPerLevelMinimum = 2, GoldPerLevelMaximum = 4, GoldBaseMinimum = 3, GoldBaseMaximum = 8;

    /// <summary>A kill's gold pile, or null: with <see cref="GoldDropChance"/>, a stack of <see cref="GoldCoin"/> of
    /// <see cref="GoldBaseMinimum"/> + <see cref="GoldPerLevelMinimum"/> × level to <see cref="GoldBaseMaximum"/> +
    /// <see cref="GoldPerLevelMaximum"/> × level coins; nothing without the gold item type.</summary>
    public static Item? RollGold(IReadOnlyDictionary<string, ItemType> types, int monsterLevel, Random random)
    {
        if (!types.TryGetValue(GoldCoin, out var gold) || random.Next(100) >= GoldDropChance) return null;
        int level = Math.Max(1, monsterLevel);
        return Create(gold, random.Next(GoldBaseMinimum + GoldPerLevelMinimum * level, GoldBaseMaximum + GoldPerLevelMaximum * level + 1));
    }

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
