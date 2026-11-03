using GameServer.Core.Accounts;

namespace GameServer.Core.Tests;

/// <summary>PoE2's Characters.datc64 rows (PORTING-POE2.md, wave 16).</summary>
public class Poe2ClassesTests
{
    [Fact]
    public void Every_row_is_its_own_index_and_every_class_has_one_row()
    {
        Assert.Equal(12, Poe2Classes.Rows.Count);
        for (int i = 0; i < Poe2Classes.Rows.Count; i++) Assert.Equal(i, Poe2Classes.Rows[i].Index);
        Assert.Equal(Poe2Classes.Rows.Count, Poe2Classes.Rows.Select(r => r.Class).Distinct().Count());
    }

    [Theory]
    [InlineData(CharacterClass.Warrior, 6, "Metadata/Characters/Str/StrFourb")]
    [InlineData(CharacterClass.Sorceress, 7, "Metadata/Characters/Int/IntFourb")]
    [InlineData(CharacterClass.Huntress, 8, "Metadata/Characters/Dex/DexFourb")]
    [InlineData(CharacterClass.Mercenary, 9, "Metadata/Characters/StrDex/StrDexFourb")]
    [InlineData(CharacterClass.Monk, 10, "Metadata/Characters/DexInt/DexIntFourb")]
    [InlineData(CharacterClass.Druid, 11, "Metadata/Characters/StrInt/StrIntFourb")]
    [InlineData(CharacterClass.Witch, 1, "Metadata/Characters/Int/IntFour")]
    [InlineData(CharacterClass.Ranger, 2, "Metadata/Characters/Dex/DexFour")]
    public void A_class_has_its_row_and_object_type(CharacterClass cls, byte row, string objectType)
    {
        Assert.Equal(row, Poe2Classes.Of(cls).Index);
        Assert.Equal(objectType, Poe2Classes.Of(cls).ObjectType);
        Assert.Equal(cls, Poe2Classes.ClassOfRow(row));
    }

    [Theory]
    [InlineData("StrFourDefault", CharacterClass.Marauder)]
    [InlineData("IntFourDefault", CharacterClass.Witch)]
    [InlineData("StrDexFourDefault", CharacterClass.Duelist)]
    [InlineData("StrFourBDefault", CharacterClass.Warrior)]
    [InlineData("IntFourBDefault", CharacterClass.Sorceress)]
    [InlineData("DexFourBDefault", CharacterClass.Huntress)]
    [InlineData("StrDexFourBDefault", CharacterClass.Mercenary)]
    [InlineData("DexIntFourBDefault", CharacterClass.Monk)]
    [InlineData("StrIntFourBDefault", CharacterClass.Druid)]
    public void A_default_start_state_names_its_class(string startState, CharacterClass cls)
    {
        Assert.Equal(cls, Poe2Classes.ClassOfStartState(startState));
    }

    [Theory]
    [InlineData("WitchActTwo")]
    [InlineData("intfourdefault")]
    [InlineData("")]
    public void Other_start_states_name_no_class(string startState)
    {
        Assert.Null(Poe2Classes.ClassOfStartState(startState));
    }

    [Fact]
    public void The_scion_gets_the_witch_and_unknown_rows_no_class()
    {
        Assert.Equal(CharacterClass.Witch, Poe2Classes.Of(CharacterClass.Scion).Class);
        Assert.Null(Poe2Classes.ClassOfRow(12));
        Assert.Null(Poe2Classes.ClassOfRow(-1));
    }
}
