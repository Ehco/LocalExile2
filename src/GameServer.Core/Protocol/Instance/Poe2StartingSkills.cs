using GameServer.Core.Accounts;
using GameServer.Core.World;

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
/// A class with a starting weapon swings it with Default Attack (GrantedEffects MeleePlayer, ActiveSkills player_melee,
/// GEPL and GESSPL row 0), whose ActiveSkillWeaponRequirement 36 "Any Melee Weapon and Unarmed" takes the one hand mace:
/// the attack takes the weapon's values, and main_hand_weapon_type is the weapon's WieldableClasses row (0 unarmed, 5 one
/// hand mace), the index of the client's per-weapon tables at 0x142f65698-0x142f65737. With a weapon in hand the client
/// never sends Punch (an Unarmed skill), and the weapon's own basic attack (the Wooden Club's Mace Strike, GEPL and GESSPL
/// row 400, <see cref="Poe2Items.ItemType.Attack"/>) stays greyed out ("[Not enough mana]") although the club counts as
/// usable and the Actor's hand types are sent; Default Attack hit and killed zombies live (2026-11-04). PORTING-POE2.md
/// wave 26.
/// </summary>
public static class Poe2StartingSkills
{
    public const uint PunchEffectsPerLevelRow = 40, PunchStatSetsPerLevelRow = 40;
    public const uint DefaultAttackEffectsPerLevelRow = 0, DefaultAttackStatSetsPerLevelRow = 0;
    public const uint DodgeRollEffectsPerLevelRow = 899, DodgeRollStatSetsPerLevelRow = 905;
    public const uint PunchId = ActorSkillsPoe2.FirstSkillId, DodgeRollId = ActorSkillsPoe2.FirstSkillId + 1;

    /// <summary>The weapon's own basic attack (Mace Strike for a mace: the item type's <see cref="Poe2Items.BasicAttack"/>),
    /// granted beside Default Attack with the same weapon values (wave 28).</summary>
    public const uint WeaponAttackId = ActorSkillsPoe2.FirstSkillId + 2;

    /// <summary>The skill record's source byte for a skill of weapon set I: bits 7-6 the set (1 = I, 2 = II), bits 5-0 the
    /// hand record. The client's weapon check (0x141e89b60 / 0x141d82bc0) takes the hand types of that record (the
    /// Actor's +0x2c8 + 0x28 × record); a skill of set 0 gets the Actor's defaults (+0x924: Unarmed, none), which is
    /// what greyed the weapon's attack (Frida, 2026-11-04).</summary>
    public const byte WeaponSetOneSource = 0x40;

    public const uint MainHandMinimumPhysicalDamage = 0x10f, MainHandMaximumPhysicalDamage = 0x110;
    public const uint MainHandMinimumTotalDamage = 0x291, MainHandMaximumTotalDamage = 0x292;
    public const uint MainHandBaseWeaponAttackDurationMs = 0x138;
    public const uint HundredTimesAttacksPerSecond = 0x2b3, HundredTimesDamagePerSecond = 0x2b4;
    public const uint DisplayHundredTimesDamagePerSkillUse = 0x7c1, SkillUsesMainHand = 0x1c0e;
    public const uint MainHandBaseMaximumAttackDistance = 0x13c, MainHandMaximumAttackDistance = 0x4cd;
    public const uint MainHandWeaponType = 0x3;

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
    public static IReadOnlyList<(uint Key, int Value)> UnarmedStats(Poe2Classes.Row row) =>
        WeaponStats(UnarmedMinimumDamage, UnarmedMaximumDamage(row), UnarmedAttackMs, UnarmedAttackDistance);

    /// <summary>The main hand's final stats: the weapon's when it has values, else unarmed.</summary>
    public static IReadOnlyList<(uint Key, int Value)> MainHandStats(Poe2Classes.Row row, Poe2Items.ItemType? weapon) =>
        weapon?.Weapon is { } v
            ? [.. WeaponStats(v.MinimumDamage, v.MaximumDamage, v.AttackMs, v.Range), (MainHandWeaponType, weapon.Wieldable ?? 0)]
            : UnarmedStats(row);

    static IReadOnlyList<(uint Key, int Value)> WeaponStats(int min, int max, int attackMs, int range)
    {
        int aps100 = 100_000 / attackMs, average100 = (min + max) * 50;
        return
        [
            (MainHandMinimumPhysicalDamage, min), (MainHandMaximumPhysicalDamage, max),
            (MainHandMinimumTotalDamage, min), (MainHandMaximumTotalDamage, max),
            (MainHandBaseWeaponAttackDurationMs, attackMs),
            (HundredTimesAttacksPerSecond, aps100), (HundredTimesDamagePerSecond, average100 * aps100 / 100),
            (DisplayHundredTimesDamagePerSkillUse, average100), (SkillUsesMainHand, 1),
            (MainHandBaseMaximumAttackDistance, range), (MainHandMaximumAttackDistance, range),
        ];
    }

    /// <summary>The attack (Punch unarmed, Default Attack with a weapon) with the main hand's values, the dodge roll, and
    /// with a weapon its own basic attack (<see cref="WeaponAttackId"/>), for the player's Actor.</summary>
    public static IReadOnlyList<GrantedSkillPoe2> For(Poe2Classes.Row row, Poe2Items.ItemType? weapon = null) =>
    [
        weapon?.Weapon is null
            ? new(PunchId, PunchEffectsPerLevelRow, PunchStatSetsPerLevelRow, MainHandStats(row, weapon))
            : new(PunchId, DefaultAttackEffectsPerLevelRow, DefaultAttackStatSetsPerLevelRow, MainHandStats(row, weapon)),
        new(DodgeRollId, DodgeRollEffectsPerLevelRow, DodgeRollStatSetsPerLevelRow),
        .. weapon is { Weapon: not null, Attack: { } attack }
            ? new GrantedSkillPoe2[] { new(WeaponAttackId, attack.EffectsPerLevelRow, attack.StatSetsPerLevelRow, MainHandStats(row, weapon), WeaponSetOneSource) }
            : [],
    ];
}
