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
        int? Wieldable = null, FlaskValues? Flask = null, ArmourValues? Armour = null, string? ClassName = null);

    /// <summary>ArmourTypes: an armour's base defences (wave 33).</summary>
    public sealed record ArmourValues(int Armour, int Evasion, int EnergyShield);

    /// <summary>Flasks.datc64 and ComponentCharges.datc64 (wave 32): the flask's type (1 life, 2 mana), what a use
    /// recovers over <paramref name="RecoveryTenths"/> tenths of a second, the charges it holds and a use's cost.</summary>
    public sealed record FlaskValues(int Type, int LifePerUse, int ManaPerUse, int RecoveryTenths, int MaxCharges, int PerCharge);

    /// <summary>The two flasks a new PoE2 character has.</summary>
    public const string LesserLifeFlask = "Metadata/Items/Flasks/FourFlaskLife1", LesserManaFlask = "Metadata/Items/Flasks/FourFlaskMana1";

    /// <summary>A flask of the type, full of charges.</summary>
    public static Item CreateFlask(ItemType type) => Create(type) with { Charges = type.Flask?.MaxCharges ?? 0 };

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
        // BaseItemTypes repeats a few Ids (FourOneHandSword1 ...): the first row keeps the Id.
        return rows.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
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

    /// <summary>The chance (percent) that a normal monster drops a piece of equipment: our own number (wave 33).</summary>
    public const int GearDropChance = 12;

    /// <summary>The equipment bases that may drop: PoE2's own bases (their file names start with "Four", as
    /// FourOneHandMace1; the rest are PoE1's, dev items and skill-made ones), weapons, armours and jewellery.</summary>
    public static bool IsGearBase(ItemType type) =>
        type.Id.Contains("/Four", StringComparison.Ordinal)
        && (type.Weapon is not null || type.Armour is not null
            || type.Id.StartsWith("Metadata/Items/Rings/") || type.Id.StartsWith("Metadata/Items/Amulets/") || type.Id.StartsWith("Metadata/Items/Belts/"));

    /// <summary>A kill's piece of equipment, or null: with <see cref="GearDropChance"/>, one base among those the
    /// monster's level reaches (DropLevel), each as likely; normal rarity, item level the monster's. Mods come later.</summary>
    public static Item? RollGear(IReadOnlyDictionary<string, ItemType> types, int monsterLevel, Random random)
    {
        if (random.Next(100) >= GearDropChance) return null;
        var bases = types.Values.Where(t => IsGearBase(t) && t.DropLevel <= Math.Max(1, monsterLevel)).OrderBy(t => t.Row).ToList();
        if (bases.Count == 0) return null;
        var type = bases[random.Next(bases.Count)];
        return Create(type) with { ItemLevel = (byte)Math.Clamp(monsterLevel, 1, 100), RequiredLevel = type.DropLevel };
    }

    /// <summary>The chance (percent) that a normal monster drops an uncut skill gem: our own number (wave 30).</summary>
    public const int UncutGemDropChance = 4;

    /// <summary>A kill's uncut skill gem, or null: with <see cref="UncutGemDropChance"/>, the tiered Uncut Skill Gem of
    /// the highest tier the monster's level reaches; nothing without the gem table or the item type.</summary>
    public static Item? RollUncutGem(IReadOnlyDictionary<string, ItemType> types, Poe2Gems gems, int monsterLevel, Random random)
    {
        if (gems.Count == 0 || random.Next(100) >= UncutGemDropChance) return null;
        var uncut = gems.UncutGems.Where(u => u.Kind == Poe2Gems.SkillKind && u.Tier >= 1 && u.Tier <= Math.Max(1, monsterLevel)).MaxBy(u => u.Tier);
        return uncut is not null && types.TryGetValue(uncut.Id, out var type) ? Create(type) : null;
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
