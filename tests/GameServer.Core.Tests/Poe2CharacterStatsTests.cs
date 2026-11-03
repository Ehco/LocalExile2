using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>A PoE2 character's base life, mana and attributes (PORTING-POE2.md, wave 17).</summary>
public class Poe2CharacterStatsTests
{
    [Theory]
    // 16 + 12 per level + 2 per strength; 30 + 4 per level + 2 per intelligence.
    [InlineData(CharacterClass.Witch, 1, 42, 64, 7, 7, 15)]
    [InlineData(CharacterClass.Warrior, 1, 58, 48, 15, 7, 7)]
    [InlineData(CharacterClass.Huntress, 1, 42, 48, 7, 15, 7)]
    [InlineData(CharacterClass.Mercenary, 1, 50, 48, 11, 11, 7)]
    [InlineData(CharacterClass.Druid, 1, 50, 56, 11, 7, 11)]
    [InlineData(CharacterClass.Witch, 10, 150, 100, 7, 7, 15)]
    [InlineData(CharacterClass.Sorceress, 100, 1230, 460, 7, 7, 15)]
    public void A_class_has_its_base_life_mana_and_attributes_at_a_level(CharacterClass cls, int level,
        int life, int mana, int strength, int dexterity, int intelligence)
    {
        var sheet = Poe2CharacterStats.For(Poe2Classes.Of(cls), level);

        Assert.Equal(new Poe2CharacterStats.Sheet(life, mana, strength, dexterity, intelligence), sheet);
    }

    [Fact]
    public void A_level_below_one_counts_as_one()
    {
        var witch = Poe2Classes.Of(CharacterClass.Witch);

        Assert.Equal(Poe2CharacterStats.For(witch, 1), Poe2CharacterStats.For(witch, 0));
    }

    [Fact]
    public void Without_attributes_the_sheet_is_the_clients_own_level_one_default()
    {
        // Live 2026-11-04: with no stats sent, the character panel showed 28 life and 34 mana.
        var bare = new Poe2Classes.Row(1, CharacterClass.Witch, "Metadata/Characters/Int/IntFour", 0, 0, 0);

        var sheet = Poe2CharacterStats.For(bare, 1);

        Assert.Equal(28, sheet.MaximumLife);
        Assert.Equal(34, sheet.MaximumMana);
    }

    [Fact]
    public void The_stats_are_the_five_values_by_client_key()
    {
        var sheet = new Poe2CharacterStats.Sheet(MaximumLife: 42, MaximumMana: 64, Strength: 7, Dexterity: 8, Intelligence: 15);

        Assert.Equal(new (uint, int)[] { (0xef, 42), (0xf0, 64), (0x233, 7), (0x239, 8), (0x236, 15) }, sheet.Stats);
    }
}
