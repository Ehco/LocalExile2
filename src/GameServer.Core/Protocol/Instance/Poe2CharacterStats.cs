using GameServer.Core.Accounts;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A PoE2 character's base life, mana and attributes, before passives, items and buffs, sent as the player's Stats
/// (<see cref="PlayerObjectPoe2.Stats"/>). The client derives none of them: without these stats its character panel
/// shows 28 life and 34 mana with zero attributes at level 1, and with the attributes alone still 28 and 34 (live
/// 2026-11-04). It shows the maximum life, maximum mana and attributes sent.
/// <list type="bullet">
/// <item>life = Characters BaseMaxLife (16, every class) + Character.ot life_per_level (12) × level + 2 per strength;</item>
/// <item>mana = BaseMaxMana (30) + mana_per_level (4) × level + 2 per intelligence;</item>
/// <item>attributes = the class row's base (<see cref="Poe2Classes.Row"/>); Character.ot gives none per level.</item>
/// </list>
/// The per-attribute bonuses are the client's own attribute tooltips (0.5.5e). Dexterity's +8 accuracy per point is not
/// sent: the accuracy stat key is not mapped.
/// </summary>
public static class Poe2CharacterStats
{
    // Client stat keys: PoE2 Stats.datc64 row + 1 (checked live against the character panel).
    public const uint MaximumLife = 0xef, MaximumMana = 0xf0;
    public const uint Strength = 0x233, Intelligence = 0x236, Dexterity = 0x239;

    public const int BaseLife = 16, BaseMana = 30, LifePerLevel = 12, ManaPerLevel = 4;
    public const int LifePerStrength = 2, ManaPerIntelligence = 2;

    /// <param name="Extra">The other stats the modifiers add (passives: damage %, armour % ...), summed by client
    /// key and sent as they are.</param>
    public sealed record Sheet(int MaximumLife, int MaximumMana, int Strength, int Dexterity, int Intelligence,
        IReadOnlyList<(uint Key, int Value)>? Extra = null)
    {
        /// <summary>The (key, value) pairs of the player's Stats component.</summary>
        public IReadOnlyList<(uint Key, int Value)> Stats =>
        [
            (Poe2CharacterStats.MaximumLife, MaximumLife), (Poe2CharacterStats.MaximumMana, MaximumMana),
            (Poe2CharacterStats.Strength, Strength), (Poe2CharacterStats.Dexterity, Dexterity),
            (Poe2CharacterStats.Intelligence, Intelligence),
            .. Extra ?? [],
        ];
    }

    /// <summary>The class's sheet at a level; a level below 1 counts as 1.</summary>
    public static Sheet For(Poe2Classes.Row row, int level) => For(row, level, null);

    // The modifier stats the sheet folds into its own numbers (the passive tree's, wave 28); every other goes out raw.
    const string BaseStrength = "base_strength", BaseDexterity = "base_dexterity", BaseIntelligence = "base_intelligence",
        AllAttributes = "additional_all_attributes", BaseLifeStat = "base_maximum_life", LifePercent = "maximum_life_+%",
        BaseManaStat = "base_maximum_mana", ManaPercent = "maximum_mana_+%", AttributeText = "display_passive_attribute_text";

    /// <summary>The class's sheet at a level with <paramref name="modifiers"/> (Stats Id, client key, value) added:
    /// the attribute flats, the flat and percent life and mana (life = (base + per level + 2 × strength + flats) ×
    /// (100 + %) / 100, mana likewise with intelligence), and every other stat summed by key in <see cref="Sheet.Extra"/>.</summary>
    public static Sheet For(Poe2Classes.Row row, int level, IEnumerable<(string Id, uint Key, int Value)>? modifiers)
    {
        level = Math.Max(1, level);
        var sums = new Dictionary<string, int>();
        var extra = new Dictionary<uint, int>();
        foreach (var (id, key, value) in modifiers ?? [])
        {
            if (id is BaseStrength or BaseDexterity or BaseIntelligence or AllAttributes or BaseLifeStat or LifePercent or BaseManaStat or ManaPercent)
                sums[id] = sums.GetValueOrDefault(id) + value;
            else if (id != AttributeText) extra[key] = extra.GetValueOrDefault(key) + value;
        }
        int all = sums.GetValueOrDefault(AllAttributes);
        int strength = row.Strength + sums.GetValueOrDefault(BaseStrength) + all;
        int dexterity = row.Dexterity + sums.GetValueOrDefault(BaseDexterity) + all;
        int intelligence = row.Intelligence + sums.GetValueOrDefault(BaseIntelligence) + all;
        int life = (BaseLife + LifePerLevel * level + LifePerStrength * strength + sums.GetValueOrDefault(BaseLifeStat))
            * (100 + sums.GetValueOrDefault(LifePercent)) / 100;
        int mana = (BaseMana + ManaPerLevel * level + ManaPerIntelligence * intelligence + sums.GetValueOrDefault(BaseManaStat))
            * (100 + sums.GetValueOrDefault(ManaPercent)) / 100;
        return new Sheet(life, mana, strength, dexterity, intelligence, extra.Count == 0 ? null : [.. extra.Select(e => (e.Key, e.Value))]);
    }
}
