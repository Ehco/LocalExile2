using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>PoE2's skill gems: the gem table, the gem window's cut request, the Actor skill update and the session's
/// skill slots (PORTING-POE2.md, wave 30; written without a live test).</summary>
public class InstanceGemsPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    static readonly IReadOnlyList<GrantedSkillPoe2> WarriorSkills = Poe2StartingSkills.For(Poe2Classes.Of(CharacterClass.Warrior));
    const uint Player = InstanceSessionPoe2.DefaultPlayerId;
    const uint MainInventory = 1, SkillSlots = 63;

    // Rolling Slam (BaseItemTypes 589) at gem level 1: GEPL 6113, GESSPL 7797 and the two slams' 7837 / 7877; the Uncut
    // Skill Gem (Level 1) is BaseItemTypes 4902.
    static readonly Poe2Gems.Gem RollingSlam = new("Metadata/Items/Gems/SkillGemRollingSlam", 589, "Rolling Slam", 984173153, 440, 204, 1,
        Poe2Gems.SkillKind, 1, 100, 0, 0, new Poe2Gems.Effect(175, "RollingSlamPlayer", 840600159, "rolling_slam", "Rolling Slam"),
        [new Poe2Gems.Level(1, 6113, [7797, 7837, 7877]), new Poe2Gems.Level(2, 6114, [7798, 7838, 7878])]);
    static readonly Poe2Gems.Uncut UncutOne = new("Metadata/Items/Gems/SkillGemUncut1", 4902, "Uncut Skill Gem (Level 1)", 1279452488, 1, Poe2Gems.SkillKind);
    const string Warrior = "Metadata/Characters/Str/StrFourb";
    static readonly Poe2Gems Table = new([RollingSlam], [UncutOne], new Dictionary<string, int> { [Warrior] = 589 });

    static readonly Poe2Items.ItemType UncutType = new(UncutOne.Id, UncutOne.Row, UncutOne.Name, UncutOne.Hash, 1, 1, 1, 16301, ["Base", "Usable", "Stack"], 1);
    static readonly Poe2Items.ItemType GemType = new(RollingSlam.Id, RollingSlam.Row, RollingSlam.Name, RollingSlam.Hash, 1, 1, 1, 9450, ["Base", "SkillGem", "Quality", "Sockets"]);
    static readonly IReadOnlyDictionary<string, Poe2Items.ItemType> Types = new Dictionary<string, Poe2Items.ItemType> { [UncutType.Id] = UncutType, [GemType.Id] = GemType };

    static InstanceSessionPoe2 InWorld(IEnumerable<int>? gems = null, List<IReadOnlyCollection<int>>? saved = null, bool startingUncut = true,
        string objectType = InstanceSessionPoe2.DefaultObjectType, bool startingGem = false)
    {
        var player = new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Mana: 48, ClassRow: 6,
            Skills: [.. WarriorSkills, .. InstanceGemsPoe2.Skills(Table, gems ?? [])]);
        var session = new InstanceSessionPoe2(player, objectType: objectType, itemTypes: Types, gems: gems, onGems: saved is null ? null : saved.Add,
            gemTable: Table, startingGem: startingGem, startingUncutGem: startingUncut);
        session.Handle(AreaGenerated, 0);
        return session;
    }

    static byte[] Cut(uint key, uint inventory, uint item) =>
        Convert.FromHexString("005F00" + key.ToString("X8") + inventory.ToString("X8") + item.ToString("X8"));

    static IEnumerable<string> Ids(IEnumerable<byte[]> messages) => messages.Select(m => Convert.ToHexString(m, 0, 2));

    [Fact]
    public void The_cut_requests_are_read()
    {
        Assert.Equal(new InstanceGemsPoe2.CutGem(589, 1, 3, null), InstanceGemsPoe2.ReadCutGem(Cut(589, 1, 3)));
        Assert.Equal(new InstanceGemsPoe2.CutGem(589, 1, 3, 2),
            InstanceGemsPoe2.ReadCutGem(Convert.FromHexString("006000" + "00000001" + "00000003" + "00000002" + "0000024D" + "00")));
        Assert.Null(InstanceGemsPoe2.ReadCutGem(Convert.FromHexString("005E0001")));
    }

    [Fact]
    public void A_gem_skill_carries_its_additional_stat_sets()
    {
        var skill = InstanceGemsPoe2.Skill(RollingSlam, 1, 2)!;

        Assert.Equal(InstanceGemsPoe2.FirstGemSkillId + 2, skill.Id);
        Assert.Equal((6113u, 7797u), (skill.EffectsPerLevelRow, skill.StatSetsPerLevelRow));
        Assert.Equal([7837u, 7877u], skill.AdditionalStatSetRows);
        var w = new BlobWriter();
        ActorSkillsPoe2.WriteRecord(w, skill);
        var expected = new BlobWriter().U32(skill.Id).U8(0).U8(0).U32(6113).U8(0).U32(7797).U8(0).U32(7837).U8(0).U32(7877)
            .U8(0).U8(0).U32(0).U32(0).U32(0).U8(0).Stats(null).Stats(null).Stats(null).U8(0);
        Assert.Equal(expected.ToArray(), w.ToArray());
        Assert.Equal([InstanceGemsPoe2.FirstGemSkillId + 1], InstanceGemsPoe2.Skills(Table, [Poe2Gems.Encode(589, 1, 1), Poe2Gems.Encode(1, 1, 0)]).Select(s => s.Id));
    }

    [Fact]
    public void The_actor_update_flags_a_skill_list()
    {
        var skills = new[] { WarriorSkills[0] };

        byte[] m = InstanceGemsPoe2.ActorSkills(Player, skills);

        var list = new BlobWriter().U8(InstanceGemsPoe2.ActorHasSkills);
        ActorSkillsPoe2.WriteList(list, skills);
        Assert.Equal(InstanceProgressionPoe2.ComponentUpdate(Player, InstanceGemsPoe2.ActorComponent, list.ToArray()), m);
        Assert.Equal("02F9", Convert.ToHexString(m, 0, 2));
    }

    [Fact]
    public void A_new_character_starts_with_its_class_gem_socketed()
    {
        var saved = new List<IReadOnlyCollection<int>>();
        var session = InWorld(saved: saved, startingUncut: false, objectType: Warrior, startingGem: true);

        var entering = session.EnterWorld();

        Assert.Equal(RollingSlam.Hash, Assert.Single(session.SkillSlots).Item.TypeHash);
        Assert.Equal([Poe2Gems.Encode(589, 1, 0)], session.Gems);
        Assert.Equal([Poe2Gems.Encode(589, 1, 0)], Assert.Single(saved));
        Assert.Empty(session.MainInventory);
        var picker = entering.Single(m => Convert.ToHexString(m, 0, 2) == "0066");
        Assert.Contains(InstanceGemsPoe2.FirstGemSkillId.ToString("X8"), Convert.ToHexString(picker));
        Assert.Null(Table.StartingGem(InstanceSessionPoe2.DefaultObjectType));
        Assert.Empty(InWorld(startingUncut: false, startingGem: true).SkillSlots);   // a class without a starting gem
    }

    [Fact]
    public void A_character_without_gems_enters_with_an_uncut_gem()
    {
        var session = InWorld();

        var entry = Assert.Single(session.MainInventory);
        Assert.Equal(UncutOne.Hash, entry.Item.TypeHash);
        Assert.Empty(session.SkillSlots);
        Assert.Contains("00DE", Ids(session.EnterWorld()));
    }

    [Fact]
    public void Cutting_the_uncut_gem_sockets_the_gem_and_grants_its_skill()
    {
        var saved = new List<IReadOnlyCollection<int>>();
        var session = InWorld(saved: saved);
        uint item = session.MainInventory.Single().Id;

        var reply = session.Handle(Cut((uint)RollingSlam.Row, MainInventory, item), 0);

        Assert.Equal(["00DE", "00DE", "02F9", "0066"], Ids(reply));
        Assert.Equal(InstanceProtocolPoe2.SkillList([(InstanceGemsPoe2.FirstGemSkillId, 0)], merge: true), reply[^1]);
        Assert.Empty(session.MainInventory);
        Assert.Equal(RollingSlam.Hash, Assert.Single(session.SkillSlots).Item.TypeHash);
        Assert.Equal([Poe2Gems.Encode(589, 1, 0)], session.Gems);
        Assert.Equal([Poe2Gems.Encode(589, 1, 0)], Assert.Single(saved));
    }

    [Fact]
    public void A_cut_the_server_cannot_place_changes_nothing()
    {
        var session = InWorld();
        uint item = session.MainInventory.Single().Id;

        Assert.Empty(session.Handle(Cut(12345, MainInventory, item), 0));           // no such gem
        Assert.Empty(session.Handle(Cut((uint)RollingSlam.Row, MainInventory, item + 9), 0));   // no such item
        Assert.Empty(session.Handle(Cut((uint)RollingSlam.Row, SkillSlots, item), 0));     // not the inventory
        Assert.Single(session.MainInventory);
        Assert.Empty(session.Gems);
    }

    [Fact]
    public void Saved_gems_take_their_slots_on_entering()
    {
        var session = InWorld(gems: [Poe2Gems.Encode(589, 1, 3)]);

        Assert.Equal([Poe2Gems.Encode(589, 1, 3)], session.Gems);
        Assert.Equal(RollingSlam.Hash, Assert.Single(session.SkillSlots).Item.TypeHash);
        Assert.Empty(session.MainInventory);   // no uncut gem for a character with a gem
        Assert.Contains("00DE", Ids(session.EnterWorld()));
    }

    [Fact]
    public void The_gem_table_resolves_the_windows_key_and_encodes_sockets()
    {
        Assert.Same(RollingSlam, Table.Resolve(589));
        Assert.Same(RollingSlam, Table.Resolve(984173153));
        Assert.Same(RollingSlam, Table.Resolve(440));
        Assert.Same(RollingSlam, Table.Resolve(204));
        Assert.Null(Table.Resolve(7));
        Assert.Equal((589, 2, 5), Poe2Gems.Decode(Poe2Gems.Encode(589, 2, 5)));
        Assert.Equal(RollingSlam.Levels[1], RollingSlam.At(7));
        Assert.Equal(UncutOne, Table.UncutFor(Poe2Gems.SkillKind, 1));
        Assert.Null(Table.UncutFor(Poe2Gems.SupportKind, 1));
    }

    [Fact]
    public void The_gem_table_loads_from_its_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gems-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {"skills":[
             {"id":"Metadata/Items/Gems/SkillGemRollingSlam","row":589,"name":"Rolling Slam","hash":984173153,"skillGemsRow":440,"gemEffectsRow":204,"colour":1,"kind":"skill","gemType":0,"minLevel":1,"strength":100,"dexterity":0,"intelligence":0,"tags":[9,24],"effect":{"row":175,"id":"RollingSlamPlayer","hash":840600159,"activeSkill":"rolling_slam","skillName":"Rolling Slam"},"levels":[[1,6113,[7797,7837,7877]]]}
            ],"uncut":[
             {"id":"Metadata/Items/Gems/SkillGemUncut1","row":4902,"name":"Uncut Skill Gem (Level 1)","hash":1279452488,"tier":1,"kind":"skill"}
            ],"starting":[
             {"character":"Metadata/Characters/Str/StrFourb","gem":589,"gemId":"Metadata/Items/Gems/SkillGemRollingSlam"}
            ]}
            """);
        try
        {
            var table = Poe2Gems.Load(path);

            Assert.Equal(1, table.Count);
            var gem = table.FindRow(589)!;
            Assert.Equal(RollingSlam with { Levels = gem.Levels }, gem);
            Assert.Equal([new Poe2Gems.Level(1, 6113, [7797u, 7837u, 7877u]) with { StatSetRows = gem.Levels[0].StatSetRows }], gem.Levels);
            Assert.Equal([7797u, 7837u, 7877u], gem.Levels[0].StatSetRows);
            Assert.Equal(UncutOne, Assert.Single(table.UncutGems));
            Assert.Equal(589, table.StartingGem(Warrior)!.Row);
        }
        finally { File.Delete(path); }
        Assert.Equal(0, Poe2Gems.Load(path).Count);
    }

    [Fact]
    public void A_kill_may_drop_an_uncut_gem_of_the_monsters_level()
    {
        var drops = Enumerable.Range(0, 400).Select(seed => Poe2Items.RollUncutGem(Types, Table, 2, new Random(seed))).Where(d => d is not null).ToList();

        Assert.InRange(drops.Count, 4, 40);
        Assert.All(drops, d => Assert.Equal(UncutOne.Hash, d!.TypeHash));
        Assert.Null(Poe2Items.RollUncutGem(Types, Poe2Gems.Empty, 2, new Random(1)));
        Assert.Null(Poe2Items.RollUncutGem(new Dictionary<string, Poe2Items.ItemType>(), Table, 2, new Random(drops.Count)));
    }
}
