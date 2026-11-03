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

    public sealed record Sheet(int MaximumLife, int MaximumMana, int Strength, int Dexterity, int Intelligence)
    {
        /// <summary>The (key, value) pairs of the player's Stats component.</summary>
        public IReadOnlyList<(uint Key, int Value)> Stats =>
        [
            (Poe2CharacterStats.MaximumLife, MaximumLife), (Poe2CharacterStats.MaximumMana, MaximumMana),
            (Poe2CharacterStats.Strength, Strength), (Poe2CharacterStats.Dexterity, Dexterity),
            (Poe2CharacterStats.Intelligence, Intelligence),
        ];
    }

    /// <summary>The class's sheet at a level; a level below 1 counts as 1.</summary>
    public static Sheet For(Poe2Classes.Row row, int level)
    {
        level = Math.Max(1, level);
        return new Sheet(
            BaseLife + LifePerLevel * level + LifePerStrength * row.Strength,
            BaseMana + ManaPerLevel * level + ManaPerIntelligence * row.Intelligence,
            row.Strength, row.Dexterity, row.Intelligence);
    }
}
