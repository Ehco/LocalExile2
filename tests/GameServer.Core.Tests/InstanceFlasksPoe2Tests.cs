using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>PoE2's flasks: the starting flasks, a use's charges and recovery, the charges kills give back
/// (PORTING-POE2.md, wave 32; written without a live test).</summary>
public class InstanceFlasksPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    static readonly IReadOnlyList<GrantedSkillPoe2> WarriorSkills = Poe2StartingSkills.For(Poe2Classes.Of(CharacterClass.Warrior));
    static readonly byte[] PunchZombie = Convert.FromHexString("01A500000001020400FF08000010010000" + "00D2" + "000000C8");
    const uint Player = InstanceSessionPoe2.DefaultPlayerId, FlaskBar = 12;

    static readonly Poe2Items.ItemType LifeFlask = new(Poe2Items.LesserLifeFlask, 2623, "Lesser Life Flask", 2593649162, 1, 2, 1, 36521,
        ["Base", "Quality", "LocalStats", "Charges", "Mods", "Usable", "Flask"], Flask: new(1, 50, 0, 30, 60, 10));
    static readonly Poe2Items.ItemType ManaFlask = new(Poe2Items.LesserManaFlask, 2632, "Lesser Mana Flask", 2132925928, 1, 2, 1, 36530,
        ["Base", "Quality", "LocalStats", "Charges", "Mods", "Usable", "Flask"], Flask: new(2, 0, 50, 20, 60, 10));
    static readonly IReadOnlyDictionary<string, Poe2Items.ItemType> Types = new Dictionary<string, Poe2Items.ItemType> { [LifeFlask.Id] = LifeFlask, [ManaFlask.Id] = ManaFlask };

    // A level 1 Warrior (58 life, 48 mana) with a zombie at (205, 200) that swings at time 0 and every two seconds, each
    // hit landing a second after the swing.
    static InstanceSessionPoe2 InWorld(int damage = 40, IEnumerable<SavedItem>? items = null, bool startingFlasks = true)
    {
        var variety = new Poe2Monsters.Variety("Metadata/Monsters/Zombies/CourtGuardZombieUnarmed", 1736,
            [.. MonsterObjectPoe2.BaseComponents], AttackSpeed: 2000, MaximumAttackDistance: 7, MovementSpeed: 7, MaxAgroRange: 40);
        var attack = new Poe2Monsters.Attack(damage, damage, 2000, 7, 21391, 26777);
        var zombie = new Poe2Monster(variety.Id, 1736, 2, new MonsterObjectPoe2(205, 200, 20, MonsterObjectPoe2.BaseComponents), variety, null, attack);
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Mana: 48, Skills: WarriorSkills),
            monsters: [zombie], random: new Random(1), itemTypes: Types, items: items, startingFlasks: startingFlasks);
        session.Handle(AreaGenerated, -InstanceSessionPoe2.ArrivalSeconds);
        return session;
    }

    static byte[] Use(uint inventory, uint item) => Convert.FromHexString("007000" + inventory.ToString("X8") + item.ToString("X8"));

    static IEnumerable<string> Ids(IEnumerable<byte[]> messages) => messages.Select(m => Convert.ToHexString(m, 0, 2));

    [Fact]
    public void The_use_request_is_read()
    {
        Assert.Equal(new InstanceFlasksPoe2.UseFlask(12, 7), InstanceFlasksPoe2.ReadUseFlask(Use(12, 7)));
        Assert.Null(InstanceFlasksPoe2.ReadUseFlask(Convert.FromHexString("00700000000001")));
    }

    [Fact]
    public void A_new_character_has_a_life_and_a_mana_flask_full_of_charges()
    {
        var session = InWorld();

        var flasks = session.Flasks;
        Assert.Equal([(LifeFlask.Hash, (byte)0, 60), (ManaFlask.Hash, (byte)1, 60)], flasks.Select(f => (f.Item.TypeHash, f.X, f.Item.Charges)));
        Assert.Equal(2, Ids(session.EnterWorld()).Count(id => id == "00DE"));
        Assert.Contains(session.SavedItems, s => s.InventoryId == FlaskBar && s.TypeHash == ManaFlask.Hash);
        Assert.Equal(48, session.PlayerMana);
    }

    [Fact]
    public void A_flask_use_spends_charges_and_recovers_life_over_its_time()
    {
        var session = InWorld();
        session.Tick(0);                       // the zombie swings
        session.Tick(1);                       // and hits for 40
        Assert.Equal(18, session.PlayerLife);
        uint life = session.Flasks[0].Id;

        var reply = session.Handle(Use(FlaskBar, life), 1);

        Assert.Equal(["00DE"], Ids(reply));
        Assert.Equal(50, session.Flasks.Single(f => f.Item.TypeHash == LifeFlask.Hash).Item.Charges);
        // 50 life over 3 seconds: 16 after one second, 31 after 1.9 (before the zombie's next hit at 3).
        Assert.Contains(InstanceCombatPoe2.LifeUpdate(Player, 34), session.Tick(2));
        Assert.Equal(34, session.PlayerLife);
        Assert.Contains(InstanceCombatPoe2.LifeUpdate(Player, 49), session.Tick(2.9));
        Assert.Equal(49, session.PlayerLife);
    }

    [Fact]
    public void A_mana_flask_refills_the_mana_the_server_keeps()
    {
        var session = InWorld(damage: 0);
        uint mana = session.Flasks[1].Id;
        // No mana is spent yet: a use at full mana spends charges and moves nothing.
        Assert.Equal(["00DE"], Ids(session.Handle(Use(FlaskBar, mana), 0)));
        Assert.DoesNotContain("01CF", Ids(session.Tick(3)));
        Assert.Equal(48, session.PlayerMana);
    }

    [Fact]
    public void A_flask_without_the_charges_does_nothing()
    {
        var session = InWorld(damage: 0);
        uint life = session.Flasks[0].Id;

        for (int use = 0; use < 6; use++)
        {
            Assert.Single(session.Handle(Use(FlaskBar, session.Flasks[0].Id), use));
        }

        Assert.Equal(0, session.Flasks[0].Item.Charges);
        Assert.Empty(session.Handle(Use(FlaskBar, session.Flasks[0].Id), 7));
        Assert.Empty(session.Handle(Use(FlaskBar, life + 99), 7));
        Assert.Empty(session.Handle(Use(1, session.Flasks[0].Id), 7));
    }

    [Fact]
    public void A_kill_gives_the_flasks_a_charge()
    {
        var variety = new Poe2Monsters.Variety("Metadata/Monsters/Zombies/CourtGuardZombieUnarmed", 1736, [.. MonsterObjectPoe2.BaseComponents]);
        var zombie = new Poe2Monster(variety.Id, 1736, 2, new MonsterObjectPoe2(210, 200, 1, MonsterObjectPoe2.BaseComponents), variety);
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Mana: 48, Skills: WarriorSkills),
            monsters: [zombie], random: new Random(1), itemTypes: Types, startingFlasks: true);
        session.Handle(AreaGenerated, 0);
        session.Handle(Use(FlaskBar, session.Flasks[0].Id), 0);

        session.Handle(PunchZombie, 0);
        var kill = session.Tick(1);

        Assert.Equal(51, session.Flasks.Single(f => f.Item.TypeHash == LifeFlask.Hash).Item.Charges);
        Assert.Equal(60, session.Flasks.Single(f => f.Item.TypeHash == ManaFlask.Hash).Item.Charges);   // full already
        Assert.Equal(1, Ids(kill).Count(id => id == "00DE"));
    }

    [Fact]
    public void Saved_flasks_come_back_full_in_their_cells()
    {
        var session = InWorld(items: [new SavedItem(FlaskBar, 1, 0, ManaFlask.Hash, 1)]);

        var flask = Assert.Single(session.Flasks);
        Assert.Equal((ManaFlask.Hash, (byte)1, 60), (flask.Item.TypeHash, flask.X, flask.Item.Charges));
        Assert.Empty(InWorld(startingFlasks: false).Flasks);
    }
}
