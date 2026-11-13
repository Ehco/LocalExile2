namespace GameServer.Core.Accounts;

/// <summary>
/// PoE2's Characters.datc64 (0.5.5e, 12 rows): the row the login list (0x15 B4e) and the Player component carry, the
/// class we save, and the object type the Gateway creates the player as. Rows 0 to 5 are PoE1's classes on PoE2's
/// "Four" models; rows 6 to 11 are PoE2's own classes ("Fourb"). PoE2 has no Scion: it gets the Witch's row.
/// The create screen sends the class's CharacterStartStates Id (0x12): rows 0 to 11 of that table are each class's
/// default state, "IntFourDefault" or "IntFourBDefault" (their Characters key, the u64 at byte 8, is the row number).
/// Each row's base attributes are its BaseStrength, BaseDexterity and BaseIntelligence (row bytes 68, 72, 76): 15 in the
/// class's attribute and 7 elsewhere, or 11 in each of a hybrid's two.
/// </summary>
public static class Poe2Classes
{
    public sealed record Row(byte Index, CharacterClass Class, string ObjectType, int Strength, int Dexterity, int Intelligence)
    {
        /// <summary>The class's default CharacterStartStates Id: "IntFour" gives "IntFourDefault", "IntFourb"
        /// "IntFourBDefault".</summary>
        public string StartState
        {
            get
            {
                string model = ObjectType[(ObjectType.LastIndexOf('/') + 1)..];
                return (model.EndsWith('b') ? model[..^1] + "B" : model) + "Default";
            }
        }
    }

    public static readonly IReadOnlyList<Row> Rows =
    [
        new(0, CharacterClass.Marauder, "Metadata/Characters/Str/StrFour", 15, 7, 7),
        new(1, CharacterClass.Witch, "Metadata/Characters/Int/IntFour", 7, 7, 15),
        new(2, CharacterClass.Ranger, "Metadata/Characters/Dex/DexFour", 7, 15, 7),
        new(3, CharacterClass.Duelist, "Metadata/Characters/StrDex/StrDexFour", 11, 11, 7),
        new(4, CharacterClass.Shadow, "Metadata/Characters/DexInt/DexIntFour", 7, 11, 11),
        new(5, CharacterClass.Templar, "Metadata/Characters/StrInt/StrIntFour", 11, 7, 11),
        new(6, CharacterClass.Warrior, "Metadata/Characters/Str/StrFourb", 15, 7, 7),
        new(7, CharacterClass.Sorceress, "Metadata/Characters/Int/IntFourb", 7, 7, 15),
        new(8, CharacterClass.Huntress, "Metadata/Characters/Dex/DexFourb", 7, 15, 7),
        new(9, CharacterClass.Mercenary, "Metadata/Characters/StrDex/StrDexFourb", 11, 11, 7),
        new(10, CharacterClass.Monk, "Metadata/Characters/DexInt/DexIntFourb", 7, 11, 11),
        new(11, CharacterClass.Druid, "Metadata/Characters/StrInt/StrIntFourb", 11, 7, 11),
    ];

    /// <summary>The class's row, the Witch's for a class PoE2 does not have (the Scion).</summary>
    public static Row Of(CharacterClass cls) => Rows.FirstOrDefault(r => r.Class == cls) ?? Rows[1];

    /// <summary>The class of a default CharacterStartStates Id (exact, as the client sends it), or null for another
    /// state: the act and premade states (rows 12 on) are not mapped.</summary>
    public static CharacterClass? ClassOfStartState(string id) =>
        Rows.FirstOrDefault(r => string.Equals(r.StartState, id, StringComparison.Ordinal))?.Class;

    /// <summary>The class of a Characters.datc64 row, or null for a row the table does not have.</summary>
    public static CharacterClass? ClassOfRow(int row) => row >= 0 && row < Rows.Count ? Rows[row].Class : null;
}
