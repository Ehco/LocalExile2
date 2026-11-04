using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>PoE2 experience from kills and level-ups (PORTING-POE2.md, wave 24).</summary>
public class ExperiencePoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    static readonly IReadOnlyList<GrantedSkillPoe2> WarriorSkills = Poe2StartingSkills.For(Poe2Classes.Of(CharacterClass.Warrior));
    static readonly byte[] PunchZombie = Convert.FromHexString("01A500000001020400FF08000010010000" + "00D2" + "000000C8");
    static readonly uint[] LevelStarts = [0, 30, 100];
    const uint Player = InstanceSessionPoe2.DefaultPlayerId;

    // A level 1 Warrior at (200, 200) with some experience, one zombie (life 1, level 2, 18 experience × 150 %) at
    // (210, 200) that does not fight back.
    static InstanceSessionPoe2 InWorld(uint experience, List<int>? levels = null, List<(uint Experience, uint Gold)>? progress = null)
    {
        var variety = new Poe2Monsters.Variety("Metadata/Monsters/Zombies/CourtGuardZombieUnarmed", 1736,
            [.. MonsterObjectPoe2.BaseComponents], ExperienceMultiplier: 150);
        var stats = new Poe2Monsters.LevelStats(2, 1, 0, 18, 0, 0, 0);
        var zombie = new Poe2Monster(variety.Id, 1736, 2, new MonsterObjectPoe2(210, 200, 1, MonsterObjectPoe2.BaseComponents),
            variety, stats);
        var player = new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Mana: 46, ClassRow: 6, Experience: experience,
            Skills: WarriorSkills);
        var session = new InstanceSessionPoe2(player, monsters: [zombie], random: new Random(1), levelStarts: LevelStarts,
            onLevel: levels is null ? null : levels.Add, gold: 12,
            onProgress: progress is null ? null : (e, g) => progress.Add((e, g)));
        session.Handle(AreaGenerated, 0);
        return session;
    }

    [Fact]
    public void A_kill_reports_the_experience_and_the_gold_to_save()
    {
        var progress = new List<(uint Experience, uint Gold)>();
        var session = InWorld(0, progress: progress);

        session.Handle(PunchZombie, 0);
        session.Tick(1);

        Assert.Equal([(27u, 12u)], progress);
    }

    [Fact]
    public void The_experience_is_a_player_component_update()
    {
        Assert.Equal("02F9" + "00001000" + "00000000" + "00" + "0000" + "07" + "06" + "0100" + "1B000000",
            Convert.ToHexString(InstanceProgressionPoe2.PlayerExperience(0x1000, 27)));
    }

    [Fact]
    public void A_kill_gives_the_monsters_experience()
    {
        var session = InWorld(0);

        session.Handle(PunchZombie, 0);
        var kill = session.Tick(1);

        Assert.Equal(InstanceProgressionPoe2.PlayerExperience(Player, 27), kill[^1]);
        Assert.Equal(27u, session.Experience);
        Assert.Equal(1, session.Level);
    }

    [Fact]
    public void A_new_level_raises_and_refills_life_and_mana()
    {
        var levels = new List<int>();
        var session = InWorld(10, levels);

        session.Handle(PunchZombie, 0);
        var kill = session.Tick(1);

        // Level 2 Warrior: 16 + 12 × 2 + 2 × 15 strength life, 30 + 4 × 2 + 2 × 7 intelligence mana.
        Assert.Equal([InstanceProgressionPoe2.PlayerExperience(Player, 37),
            InstanceCombatPoe2.StatsUpdate(Player, Poe2CharacterStats.For(Poe2Classes.Of(CharacterClass.Warrior), 2).Stats),
            InstanceCombatPoe2.LifeUpdate(Player, 70), InstanceCombatPoe2.LifeUpdate(Player, 52, InstanceCombatPoe2.ManaPool)],
            kill.TakeLast(5).Take(4));
        Assert.Equal("003A", Convert.ToHexString(kill[^1], 0, 2));   // the passive tree with the new level (wave 28)
        Assert.Equal(2, session.Level);
        Assert.Equal(70, session.PlayerLife);
        Assert.Equal([2], levels);
    }

    [Fact]
    public void Experience_stops_at_the_last_levels_start()
    {
        var session = InWorld(95);

        session.Handle(PunchZombie, 0);
        var kill = session.Tick(1);

        Assert.Equal(100u, session.Experience);
        Assert.Contains(InstanceProgressionPoe2.PlayerExperience(Player, 100), kill);
    }
}
