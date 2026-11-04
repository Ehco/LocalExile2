using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>PoE2 monsters chasing and hitting the player, and the player's death (PORTING-POE2.md, wave 23).</summary>
public class MonsterAiPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    static readonly IReadOnlyList<GrantedSkillPoe2> WarriorSkills = Poe2StartingSkills.For(Poe2Classes.Of(CharacterClass.Warrior));
    const uint Zombie = 0x1001;

    // The player (life 58) at (200, 200), one zombie at (x, 200): aggro range 40 cells, 7 cells/s, reach 7 (+2), a
    // swing of 2 s that deals damage..damage.
    static InstanceSessionPoe2 InWorld(int x, int damage = 10, int aggro = 40)
    {
        var variety = new Poe2Monsters.Variety("Metadata/Monsters/Zombies/CourtGuardZombieUnarmed", 1736,
            [.. MonsterObjectPoe2.BaseComponents], AttackSpeed: 2000, MaximumAttackDistance: 7, MovementSpeed: 7, MaxAgroRange: aggro);
        var attack = new Poe2Monsters.Attack(damage, damage, 2000, 7, 21391, 26777);
        var zombie = new Poe2Monster(variety.Id, 1736, 2, new MonsterObjectPoe2(x, 200, 20, MonsterObjectPoe2.BaseComponents),
            variety, null, attack);
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Skills: WarriorSkills),
            monsters: [zombie], random: new Random(1));
        session.Handle(AreaGenerated, -InstanceSessionPoe2.ArrivalSeconds);    // the monsters act from time 0
        return session;
    }

    static byte[] Swing(int x) => InstanceSkillsPoe2.ActionStarted(Zombie, x, 200, 200, 200, ActorSkillsPoe2.FirstSkillId,
        InstanceSessionPoe2.MonsterActionFlags, InstanceSessionPoe2.DefaultPlayerId);

    [Fact]
    public void A_monster_out_of_its_aggro_range_stays_put()
    {
        var session = InWorld(260);

        Assert.Empty(session.Tick(0));
        Assert.Empty(session.Tick(5));
    }

    [Fact]
    public void A_monster_in_range_walks_to_the_edge_of_its_reach()
    {
        var session = InWorld(230);

        var chase = session.Tick(0);

        // 30 cells away, reach 9: it stops 8 cells from the player, 22 cells at 7 cells/s.
        Assert.Equal(InstanceMovementPoe2.MoveTo(Zombie, 208, 200, 22f / 7), chase.Single());
        Assert.Equal((219, 200), session.MonsterCell(0, 11.0 / 7));
        Assert.Empty(session.Tick(1));     // still walking to the same place
    }

    [Fact]
    public void In_reach_it_swings_and_its_hit_takes_the_players_life()
    {
        var session = InWorld(205);

        Assert.Equal(Swing(205), session.Tick(0).Single());
        Assert.Empty(session.Tick(0.9));
        Assert.Equal(InstanceCombatPoe2.LifeUpdate(InstanceSessionPoe2.DefaultPlayerId, 48), session.Tick(1).Single());
        Assert.Equal(48, session.PlayerLife);
        Assert.Empty(session.Tick(1.9));   // the swing lasts 2 s
        Assert.Equal(Swing(205), session.Tick(2).Single());
    }

    [Fact]
    public void At_zero_life_the_player_is_dead_and_the_monsters_stop()
    {
        var session = InWorld(205, damage: 60);

        session.Tick(0);
        var killed = session.Tick(1);
        var walk = session.Handle(Convert.FromHexString("0063000000000000"), 1.5);

        Assert.Equal([InstanceCombatPoe2.LifeUpdate(InstanceSessionPoe2.DefaultPlayerId, 0), InstanceCombatPoe2.DeathScreen()], killed);
        Assert.Equal("006D0000", Convert.ToHexString(killed[1]));
        Assert.Equal(0, session.PlayerLife);
        Assert.Empty(walk);
        Assert.Empty(session.Tick(5));
    }

    [Fact]
    public void Resurrect_at_checkpoint_revives_the_player_where_it_entered()
    {
        var session = InWorld(205, damage: 60);
        session.Tick(0);
        session.Tick(1);

        var revive = session.Handle(Convert.FromHexString("006C0000"), 3);

        Assert.Equal([InstanceCombatPoe2.DeathScreen(InstanceCombatPoe2.HideDeathScreen),
            InstanceCombatPoe2.LifeUpdate(InstanceSessionPoe2.DefaultPlayerId, 58),
            InstanceMovementPoe2.Teleport(InstanceSessionPoe2.DefaultPlayerId, 200, 200)], revive);
        Assert.Equal("006D0008", Convert.ToHexString(revive[0]));
        Assert.Equal("01DD" + "00001000" + "00000000" + "00" + "0000" + "000000C8" + "000000C8" + "7FFFFFFF" + "00000000",
            Convert.ToHexString(revive[2]));
        Assert.Equal(58, session.PlayerLife);
        Assert.Empty(session.Tick(4));                        // calm for the arrival time
        Assert.Equal(Swing(205), session.Tick(5).Single());
    }

    [Fact]
    public void Monsters_wait_for_the_client_to_load_and_while_it_is_paused()
    {
        var session = InWorld(205);

        Assert.Empty(session.Tick(-0.1));
        session.Handle(Convert.FromHexString("01FF0001"), 0);     // pause
        Assert.Empty(session.Tick(5));
        session.Handle(Convert.FromHexString("01FF0000"), 5);     // resume

        Assert.Equal(Swing(205), session.Tick(5).Single());
    }

    [Fact]
    public void A_hit_from_the_player_provokes_a_monster_beyond_its_aggro_range()
    {
        var session = InWorld(210, aggro: 5);
        var punch = Convert.FromHexString("01A500000001020400FF08000010010000" + "00D2" + "000000C8");

        Assert.Empty(session.Tick(0));
        session.Handle(punch, 0.1);
        var chase = session.Tick(0.3);

        // The player's estimate is still at its start (the approach is placed at the hit): it walks 2 cells to reach.
        Assert.Equal(InstanceMovementPoe2.MoveTo(Zombie, 208, 200, 2f / 7), chase.Single());
    }
}
