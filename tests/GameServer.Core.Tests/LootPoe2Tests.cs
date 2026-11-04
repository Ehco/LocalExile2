using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>PoE2 monster drops on the ground (PORTING-POE2.md, wave 25).</summary>
public class LootPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    static readonly IReadOnlyList<GrantedSkillPoe2> WarriorSkills = Poe2StartingSkills.For(Poe2Classes.Of(CharacterClass.Warrior));
    static readonly byte[] PunchZombie = Convert.FromHexString("01A500000001020400FF08000010010000" + "00D2" + "000000C8");
    static readonly Poe2Items.ItemType Wisdom = new("Metadata/Items/Currency/CurrencyIdentification", 2, "Scroll of Wisdom",
        1351093167, 1, 1, 1, 2083, ["Base", "Usable", "Stack"], 40);

    // One zombie (life 1, level 2) at (210, 200) that does not fight back; Scroll of Wisdom is the only drop.
    static InstanceSessionPoe2 InWorld(int seed)
    {
        var zombie = new Poe2Monster("Metadata/Monsters/Zombies/CourtGuardZombieUnarmed", 1736, 2,
            new MonsterObjectPoe2(210, 200, 1, MonsterObjectPoe2.BaseComponents));
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Skills: WarriorSkills),
            monsters: [zombie], random: new Random(seed), itemTypes: new Dictionary<string, Poe2Items.ItemType> { [Wisdom.Id] = Wisdom });
        session.Handle(AreaGenerated, 0);
        return session;
    }

    [Fact]
    public void A_currency_item_is_its_hash_base_and_stack()
    {
        var item = Poe2Items.Create(Wisdom, 3);

        Assert.Equal("AF0B8850" + "0000" + "2308" + "00" + "00" + "0000" + "00" + "0300", Convert.ToHexString(item.Payload()));
    }

    [Fact]
    public void The_ground_item_has_the_poe2_positioned_and_the_item()
    {
        var blob = new WorldItemObjectPoe2(210, 200, Poe2Items.Create(Wisdom)).Blob();

        Assert.Equal("02" + "00" + "D2000000" + "C8000000" + "00000000" + "0000" + "000000" + "00" + "00000000" + "00" + "00000000"
            + "AF0B8850" + "0000" + "2308" + "00000000" + "00" + "0100" + "00" + "00", Convert.ToHexString(blob));
    }

    [Fact]
    public void A_kill_drops_the_item_where_the_monster_fell_or_nothing()
    {
        // The punch's damage roll comes first; the drop roll follows it on the same generator.
        int dropped = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            var session = InWorld(seed);
            session.Handle(PunchZombie, 0);
            var kill = session.Tick(1);
            if (session.GroundItems.Count == 0) continue;
            dropped++;
            var (id, ground) = session.GroundItems.Single();
            Assert.Equal(session.MonsterId(1), id);
            Assert.Equal((210, 200), (ground.GridX, ground.GridY));
            Assert.Equal(InstanceProtocolPoe2.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObjectPoe2.MetadataPath),
                ground.Blob()), kill[^1]);
        }
        Assert.InRange(dropped, 5, 25);     // 35 % of 40
    }

    // A session whose kill dropped the scroll: the first seed that drops.
    static (InstanceSessionPoe2 Session, uint Id, WorldItemObjectPoe2 Ground) Dropped()
    {
        for (int seed = 0; ; seed++)
        {
            var session = InWorld(seed);
            session.Handle(PunchZombie, 0);
            session.Tick(1);
            if (session.GroundItems.Count == 1) { var (id, ground) = session.GroundItems.Single(); return (session, id, ground); }
        }
    }

    static byte[] Pick(uint id) => Convert.FromHexString("01A5" + "00" + "2000C266" + "0400" + "FF" + "08" + id.ToString("X8") + "000000D2" + "000000C8");

    [Fact]
    public void A_click_on_the_item_walks_to_it_then_puts_it_in_the_main_inventory()
    {
        var (session, id, ground) = Dropped();
        var start = session.Walk.Cell(2);

        var echo = session.Handle(Pick(id), 2);
        var early = session.Tick(2.1);
        var taken = session.Tick(3);

        Assert.Equal(InstanceSkillsPoe2.ActionStarted(InstanceSessionPoe2.DefaultPlayerId, start.X, start.Y, 210, 200, 0x2000C266, 0x0400, id),
            echo.Single());
        Assert.Empty(early);
        var entry = session.MainInventory.Single();
        Assert.Equal((0, 0), (entry.X, entry.Y));
        Assert.Equal([InstanceProtocolPoe2.RemoveObject(id),
            InstanceInventoriesPoe2.InventoryChange(1, 1, [], [new InventoryItem(entry.Id, 0, 0, ground.Item)])], taken);
        Assert.Equal("02FA" + id.ToString("X8") + "00000000" + "00" + "0000", Convert.ToHexString(taken[0]));
        Assert.Empty(session.GroundItems);
    }

    [Fact]
    public void An_inventory_change_is_poe1s_with_the_route_byte()
    {
        var item = Poe2Items.Create(Wisdom, 2);

        Assert.Equal("00DE" + "00" + "00" + "00000001" + "00000003" + "00000001" + "00000007" + "00000001"
            + "00000009" + "04" + "01" + "000F" + Convert.ToHexString(item.Payload()) + "00",
            Convert.ToHexString(InstanceInventoriesPoe2.InventoryChange(1, 3, [7], [new InventoryItem(9, 4, 1, item)])));
    }

    [Fact]
    public void Stacks_grow_before_a_new_cell_is_taken()
    {
        var inventory = new Poe2Inventory(1, 2, 1);
        inventory.Add(Poe2Items.Create(Wisdom, 39), 1, 1, 40);

        var change = inventory.Add(Poe2Items.Create(Wisdom, 3), 1, 1, 40);

        Assert.Equal(InstanceInventoriesPoe2.InventoryChange(1, 2, [1],
            [new InventoryItem(2, 0, 0, Poe2Items.Create(Wisdom, 40)), new InventoryItem(3, 1, 0, Poe2Items.Create(Wisdom, 2))]), change);
        Assert.Null(inventory.Add(Poe2Items.Create(Wisdom, 39), 1, 1, 40));    // full: nothing changes
        Assert.Equal(2, inventory.Items.Count);
    }
}
