using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>Attacks used at the ground hit the monsters in their area (PORTING-POE2.md, wave 30 follow-up): Rolling
/// Slam's two slams from its gem level's combat values, a basic attack's one hit.</summary>
public class AreaAttackPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    static readonly IReadOnlyList<GrantedSkillPoe2> WarriorSkills = Poe2StartingSkills.For(Poe2Classes.Of(CharacterClass.Warrior));
    const uint SlamId = InstanceGemsPoe2.FirstGemSkillId;

    // Rolling Slam level 1 as gems.json has it: +1000 ms, +20 action distance, the first slam at 75 % (radius 14), the
    // second at 150 % (radius 18).
    static readonly Poe2Gems.Gem RollingSlam = new("Metadata/Items/Gems/SkillGemRollingSlam", 589, "Rolling Slam", 984173153, 440, 204, 1,
        Poe2Gems.SkillKind, 1, 100, 0, 0, new Poe2Gems.Effect(175, "RollingSlamPlayer", 840600159, "rolling_slam", "Rolling Slam"),
        [new Poe2Gems.Level(1, 6113, [7797, 7837, 7877], new Poe2Gems.Combat(1000, 20, [new(-2500, 14), new(5000, 18)]))]);
    static readonly Poe2Gems Table = new([RollingSlam], []);

    // The Warrior, unarmed (2–8 damage, 606 ms, reach 8), at (200, 200) with Rolling Slam in slot 0, and zombies of 100
    // life that stand still.
    static InstanceSessionPoe2 InWorld(params (int X, int Y)[] zombies)
    {
        int[] gems = [Poe2Gems.Encode(589, 1, 0)];
        var player = new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Mana: 48, ClassRow: 6,
            Skills: [.. WarriorSkills, .. InstanceGemsPoe2.Skills(Table, gems, InstanceGemsPoe2.HandOf(WarriorSkills))]);
        var monsters = zombies.Select(z => new Poe2Monster("Metadata/Monsters/Zombies/CourtGuardZombieUnarmed", 1736, 2,
            new MonsterObjectPoe2(z.X, z.Y, 100, MonsterObjectPoe2.BaseComponents))).ToList();
        var session = new InstanceSessionPoe2(player, monsters: monsters, random: new Random(1), gems: gems, gemTable: Table);
        session.Handle(AreaGenerated, 0);
        return session;
    }

    // C→S 0x1a5 without a target: route, skill, flags 0x0400, sequence 0xff, options 0, the cell relative to the player.
    static byte[] UseAt(uint skill, int x, int y) =>
        Convert.FromHexString("01A500" + skill.ToString("X8") + "0400FF00" + x.ToString("X8") + y.ToString("X8"));

    static IEnumerable<string> Ids(IEnumerable<byte[]> messages) => messages.Select(m => Convert.ToHexString(m, 0, 2));

    [Fact]
    public void Rolling_slam_at_the_ground_slams_twice_and_carries_the_player()
    {
        var session = InWorld((203, 200), (208, 200), (200, 210));

        Assert.Equal(["01C8"], Ids(session.Handle(UseAt(SlamId, 8, 0), 0)));

        // 1606 ms in all: the first slam at a third, around (201, 200) with 3.3 cells; the second at two thirds,
        // around (206, 200) with 3.7 cells.
        Assert.DoesNotContain("01CF", Ids(session.Tick(0.5)));
        Assert.Contains("01CF", Ids(session.Tick(0.6)));
        Assert.InRange(session.MonsterLife(0), 94, 98);                  // 75 %: 2–6
        Assert.Equal((100, 100), (session.MonsterLife(1), session.MonsterLife(2)));
        session.Tick(1.1);
        Assert.InRange(session.MonsterLife(0), 82, 95);                  // and 150 %: 3–12
        Assert.InRange(session.MonsterLife(1), 88, 97);
        Assert.Equal(100, session.MonsterLife(2));
        Assert.Equal((205, 200), session.Walk.Cell(1.1));               // rolled towards the aim
    }

    [Fact]
    public void A_basic_attack_at_the_ground_hits_in_front_of_the_player()
    {
        var session = InWorld((201, 200), (205, 200));

        session.Handle(UseAt(Poe2StartingSkills.PunchId, 1, 0), 0);
        session.Tick(0.31);                                              // half of 606 ms

        Assert.InRange(session.MonsterLife(0), 92, 98);
        Assert.Equal(100, session.MonsterLife(1));
        Assert.Equal((200, 200), session.Walk.Cell(0.31));
    }

    [Fact]
    public void The_dodge_roll_and_skills_without_values_hit_nothing()
    {
        var session = InWorld((201, 200));

        Assert.Empty(session.Handle(UseAt(Poe2StartingSkills.DodgeRollId, 1, 0), 0));
        session.Tick(2);

        Assert.Equal(100, session.MonsterLife(0));
    }

    [Fact]
    public void The_re_aim_of_an_action_is_framed_and_read()
    {
        byte[] aim = Convert.FromHexString("01A8000000001C0000003000000112");

        Assert.Equal((0x112u, 28, 48), InstanceSkillsPoe2.ReadAim(aim));
        Assert.Null(InstanceSkillsPoe2.ReadAim(Convert.FromHexString("01A800")));
        Assert.Equal(2, InstanceSessionPoe2.Split([.. aim, .. Convert.FromHexString("01AC00")], out int rest).Count);
        Assert.Equal(18, rest);
        Assert.Empty(InWorld().Handle(aim, 0));
    }

    [Fact]
    public void The_gem_table_loads_the_combat_values()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gems-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {"skills":[
             {"id":"Metadata/Items/Gems/SkillGemRollingSlam","row":589,"name":"Rolling Slam","hash":984173153,"kind":"skill","effect":{"row":175,"id":"RollingSlamPlayer","hash":840600159},"levels":[[1,6113,[7797,7837,7877],{"attackMs":1000,"reach":20,"hits":[[-2500,14],[5000,18]]}],[2,6114,[7798]]]}
            ],"uncut":[]}
            """);
        try
        {
            var levels = Poe2Gems.Load(path).FindRow(589)!.Levels;

            var combat = levels[0].Combat!;
            Assert.Equal((1000, 20), (combat.AddedAttackMs, combat.AddedReach));
            Assert.Equal([new Poe2Gems.Hit(-2500, 14), new Poe2Gems.Hit(5000, 18)], combat.Hits);
            Assert.Null(levels[1].Combat);
        }
        finally { File.Delete(path); }
    }
}
