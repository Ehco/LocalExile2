using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>PoE2 hits on monsters and their life (PORTING-POE2.md, wave 22).</summary>
public class InstanceCombatPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    static readonly IReadOnlyList<GrantedSkillPoe2> WarriorSkills = Poe2StartingSkills.For(Poe2Classes.Of(CharacterClass.Warrior));

    // The player at (200, 200), one zombie (life 20) at (210, 200); its id is 0x1001 (no exits).
    static InstanceSessionPoe2 InWorld(int seed = 1, int monsterLife = 20)
    {
        var zombie = new Poe2Monster("Metadata/Monsters/Zombies/CourtGuardZombieUnarmed", 1736, 2,
            new MonsterObjectPoe2(210, 200, (uint)monsterLife, MonsterObjectPoe2.BaseComponents));
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Skills: WarriorSkills),
            monsters: [zombie], random: new Random(seed));
        session.Handle(AreaGenerated, 0);
        return session;
    }

    // Punch (0x102) on object 0x1001 at its cell, as the client sends it.
    static readonly byte[] PunchZombie = Convert.FromHexString("01A500000001020400FF08000010010000" + "00D2" + "000000C8");

    [Fact]
    public void Life_update_sends_every_field_of_the_pool()
    {
        Assert.Equal("01CF" + "00001003" + "00000000" + "00" + "0000" + "F0" + "00" + "0F" + "00000000" + "00000000" + "0000",
            Convert.ToHexString(InstanceCombatPoe2.LifeUpdate(0x1003, 15)));
        Assert.Equal("F4", Convert.ToHexString(InstanceCombatPoe2.LifeUpdate(1, 0, InstanceCombatPoe2.ManaPool), 13, 1));
    }

    [Fact]
    public void Punch_is_an_attack_and_the_dodge_roll_is_not()
    {
        Assert.Equal(new AttackPoe2(2, 8, 606, 8), AttackPoe2.Of(WarriorSkills[0]));
        Assert.Null(AttackPoe2.Of(WarriorSkills[1]));
    }

    [Fact]
    public void The_hit_lands_after_the_approach_and_half_the_attack_time()
    {
        var session = InWorld();
        int damage = new Random(1).Next(2, 9);

        session.Handle(PunchZombie, 0);
        var early = session.Tick(0.3);     // 2 cells at 37 cells/s, then 0.303 s
        var hit = session.Tick(0.4);

        Assert.Empty(early);
        Assert.Equal(InstanceCombatPoe2.LifeUpdate(0x1001, 20 - damage), hit.Single());
        Assert.Equal(20 - damage, session.MonsterLife(0));
        Assert.Equal((202, 200), session.Walk.Cell(0.4));
        Assert.Empty(session.Tick(1));
    }

    [Fact]
    public void Life_stops_at_zero_and_a_dead_monster_takes_no_more_hits()
    {
        var session = InWorld(monsterLife: 1);

        session.Handle(PunchZombie, 0);
        var kill = session.Tick(1);
        var echo = session.Handle(PunchZombie, 2);

        Assert.Equal([InstanceCombatPoe2.StatsUpdate(0x1001, [(InstanceCombatPoe2.HideMiniLifeBar, 1)]),
            InstanceCombatPoe2.LifeUpdate(0x1001, 0)], kill);
        Assert.Equal("01D3" + "00001001" + "00000000" + "00" + "0000" + "00" + "01" + "A733" + "01", Convert.ToHexString(kill[0]));
        Assert.Equal(InstanceSkillsPoe2.ActionStartedId, (ushort)(echo.Single()[0] << 8 | echo.Single()[1]));
        Assert.Empty(session.Tick(5));
    }

    [Fact]
    public void Swings_follow_each_other_at_the_attack_time()
    {
        var session = InWorld();

        session.Handle(PunchZombie, 0);
        Assert.Single(session.Tick(0.4));
        session.Handle(PunchZombie, 0.4);    // the first swing ends at 0.054 + 0.606
        Assert.Empty(session.Tick(0.9));
        Assert.Single(session.Tick(1.0));
    }

    [Fact]
    public void A_move_before_the_hit_cancels_it_where_the_approach_had_got()
    {
        var session = InWorld();

        session.Handle(PunchZombie, 0);
        session.Handle(Convert.FromHexString("0063000000000000"), 0.027);    // stop, half way to the reach

        Assert.Empty(session.Tick(1));
        Assert.Equal((201, 200), session.Walk.Cell(1));
    }
}
