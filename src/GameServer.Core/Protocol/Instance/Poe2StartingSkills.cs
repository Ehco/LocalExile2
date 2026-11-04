using GameServer.Core.Accounts;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The skills every new PoE2 character has (PORTING-POE2.md wave 21), from the 0.5.5e data:
/// <list type="bullet">
/// <item>Punch, the unarmed attack: GrantedEffects MeleeUnarmedPlayer (ActiveSkills player_melee_unarmed, action
/// Melee 0xb188), GEPL row 40 and GESSPL row 40 at level 1;</item>
/// <item>the dodge roll: Characters StartingSkill, GEPL row 899 (DodgeRollPlayer, action DodgeRoll 0xed4c), GESSPL
/// row 905. It is the same for every class.</item>
/// </list>
/// The unarmed weapon is the class's: Characters WeaponSpeed (606 ms for every class), MinDamage, MaxDamage and
/// MaxAttackDistance. As in PoE1 the server sends final values in the player's and the attack's stats; the client
/// derives none of them (keys are Stats.datc64 row + 1).
/// </summary>
public static class Poe2StartingSkills
{
    public const uint PunchEffectsPerLevelRow = 40, PunchStatSetsPerLevelRow = 40;
    public const uint DodgeRollEffectsPerLevelRow = 899, DodgeRollStatSetsPerLevelRow = 905;
    public const uint PunchId = ActorSkillsPoe2.FirstSkillId, DodgeRollId = ActorSkillsPoe2.FirstSkillId + 1;

    public const uint MainHandMinimumPhysicalDamage = 0x10f, MainHandMaximumPhysicalDamage = 0x110;
    public const uint MainHandMinimumTotalDamage = 0x291, MainHandMaximumTotalDamage = 0x292;
    public const uint MainHandBaseWeaponAttackDurationMs = 0x138;
    public const uint HundredTimesAttacksPerSecond = 0x2b3, HundredTimesDamagePerSecond = 0x2b4;
    public const uint DisplayHundredTimesDamagePerSkillUse = 0x7c1, SkillUsesMainHand = 0x1c0e;
    public const uint MainHandBaseMaximumAttackDistance = 0x13c, MainHandMaximumAttackDistance = 0x4cd;

    public const int UnarmedAttackMs = 606, UnarmedAttackDistance = 8, UnarmedMinimumDamage = 2;

    /// <summary>Characters MaxDamage by row: 8 for the Marauder and the Warrior, 6 for the StrDex and StrInt classes,
    /// 5 for the others.</summary>
    public static int UnarmedMaximumDamage(Poe2Classes.Row row) => (row.Strength, row.Dexterity, row.Intelligence) switch
    {
        (15, _, _) => 8,
        (11, 11, _) or (11, _, 11) => 6,
        _ => 5,
    };

    /// <summary>The unarmed weapon's final stats: in the player's Stats and in the attack's.</summary>
    public static IReadOnlyList<(uint Key, int Value)> UnarmedStats(Poe2Classes.Row row)
    {
        int max = UnarmedMaximumDamage(row), aps100 = 100_000 / UnarmedAttackMs;
        int average100 = (UnarmedMinimumDamage + max) * 50;
        return
        [
            (MainHandMinimumPhysicalDamage, UnarmedMinimumDamage), (MainHandMaximumPhysicalDamage, max),
            (MainHandMinimumTotalDamage, UnarmedMinimumDamage), (MainHandMaximumTotalDamage, max),
            (MainHandBaseWeaponAttackDurationMs, UnarmedAttackMs),
            (HundredTimesAttacksPerSecond, aps100), (HundredTimesDamagePerSecond, average100 * aps100 / 100),
            (DisplayHundredTimesDamagePerSkillUse, average100), (SkillUsesMainHand, 1),
            (MainHandBaseMaximumAttackDistance, UnarmedAttackDistance), (MainHandMaximumAttackDistance, UnarmedAttackDistance),
        ];
    }

    /// <summary>Punch and the dodge roll, for the player's Actor.</summary>
    public static IReadOnlyList<GrantedSkillPoe2> For(Poe2Classes.Row row) =>
    [
        new(PunchId, PunchEffectsPerLevelRow, PunchStatSetsPerLevelRow, UnarmedStats(row)),
        new(DodgeRollId, DodgeRollEffectsPerLevelRow, DodgeRollStatSetsPerLevelRow),
    ];
}
