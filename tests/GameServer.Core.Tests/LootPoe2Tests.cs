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

    static readonly Poe2Items.ItemType GoldCoin = new(Poe2Items.GoldCoin, 1943, "Gold", 3718305531, 1, 1, 1, 51072,
        ["Base", "Usable", "Stack"], 10000);

    // The zombie's kill with gold piles on the table too (wave 29).
    static InstanceSessionPoe2 InWorldWithGold(int seed, List<(uint Experience, uint Gold)> progress)
    {
        var zombie = new Poe2Monster("Metadata/Monsters/Zombies/CourtGuardZombieUnarmed", 1736, 2,
            new MonsterObjectPoe2(210, 200, 1, MonsterObjectPoe2.BaseComponents));
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Skills: WarriorSkills),
            monsters: [zombie], random: new Random(seed), gold: 5, onProgress: (e, g) => progress.Add((e, g)),
            itemTypes: new Dictionary<string, Poe2Items.ItemType> { [Wisdom.Id] = Wisdom, [GoldCoin.Id] = GoldCoin });
        session.Handle(AreaGenerated, 0);
        return session;
    }

    [Fact]
    public void A_gold_pile_is_a_stack_of_coins_by_the_monsters_level()
    {
        var types = new Dictionary<string, Poe2Items.ItemType> { [GoldCoin.Id] = GoldCoin };
        var sizes = Enumerable.Range(0, 200).Select(seed => Poe2Items.RollGold(types, 2, new Random(seed))).Where(g => g is not null)
            .Select(g => (int)g!.StackSize).ToList();

        Assert.InRange(sizes.Count, 60, 140);
        Assert.All(sizes, s => Assert.InRange(s, 7, 16));
        Assert.Null(Poe2Items.RollGold(new Dictionary<string, Poe2Items.ItemType>(), 2, new Random(1)));
    }

    [Fact]
    public void A_gold_pile_taken_goes_to_the_characters_gold_not_the_inventory()
    {
        var progress = new List<(uint Experience, uint Gold)>();
        InstanceSessionPoe2? session = null; uint id = 0; WorldItemObjectPoe2? pile = null;
        for (int seed = 0; seed < 40 && pile is null; seed++)
        {
            session = InWorldWithGold(seed, progress);
            session.Handle(PunchZombie, 0);
            session.Tick(1);
            foreach (var (i, g) in session.GroundItems)
                if (g.Item.TypeHash == GoldCoin.Hash) (id, pile) = (i, g);
        }
        Assert.NotNull(pile);
        progress.Clear();

        session!.Handle(Pick(id), 2);
        var taken = session.Tick(3);

        Assert.Equal([InstanceProtocolPoe2.RemoveObject(id), InstancePassivesPoe2.Gold(5u + pile!.Item.StackSize)], taken);
        Assert.Equal(5u + pile.Item.StackSize, session.Gold);
        Assert.DoesNotContain(id, session.GroundItems.Keys);
        Assert.Empty(session.MainInventory);
        Assert.Equal([(session.Experience, session.Gold)], progress);
    }

    static readonly Poe2Items.ItemType Club = new("Metadata/Items/Weapons/OneHandWeapons/OneHandMaces/FourOneHandMace1", 2040, "Wooden Club",
        0x2a2a2a2a, 1, 3, 1, 4001, ["Base", "Mods", "Weapon", "Sockets"], Weapon: new(6, 10, 1450, 11, 500), ClassName: "One Hand Mace");
    static readonly Poe2Items.ItemType Cuirass = new("Metadata/Items/Armours/BodyArmours/FourBodyStr1", 2259, "Rusted Cuirass",
        0x2b2b2b2b, 2, 3, 1, 4002, ["Base", "Mods", "Armour", "Sockets"], Armour: new(32, 0, 0), ClassName: "Body Armour");
    static readonly Poe2Items.ItemType Hammer = new("Metadata/Items/Weapons/OneHandWeapons/OneHandMaces/FourOneHandMace2", 2041, "Smithing Hammer",
        0x2c2c2c2c, 1, 3, 4, 4003, ["Base", "Mods", "Weapon", "Sockets"], Weapon: new(10, 16, 1450, 11, 500), ClassName: "One Hand Mace");
    static readonly Poe2Items.ItemType Keyblade = new("Metadata/Items/Weapons/TwoHandWeapons/TwoHandSwords/TwoHandSwordDev", 368, "Keyblade",
        0x2d2d2d2d, 2, 4, 1, 4004, ["Base", "Mods", "Weapon", "Sockets"], Weapon: new(1, 1, 833, 16, 500), ClassName: "Two Hand Sword");
    static readonly IReadOnlyDictionary<string, Poe2Items.ItemType> Gear = new Dictionary<string, Poe2Items.ItemType>
        { [Club.Id] = Club, [Cuirass.Id] = Cuirass, [Hammer.Id] = Hammer, [Keyblade.Id] = Keyblade, [Wisdom.Id] = Wisdom };

    [Fact]
    public void A_piece_of_equipment_is_a_poe2_base_the_monsters_level_reaches()
    {
        var drops = Enumerable.Range(0, 300).Select(seed => Poe2Items.RollGear(Gear, 2, new Random(seed))).Where(d => d is not null).ToList();

        Assert.InRange(drops.Count, 15, 60);
        Assert.All(drops, d => Assert.Contains(d!.TypeHash, new[] { Club.Hash, Cuirass.Hash }));   // not the level 4 hammer, not the dev blade
        Assert.All(drops, d => Assert.Equal(2, d!.ItemLevel));
        Assert.Contains(drops, d => d!.TypeHash == Cuirass.Hash);
        Assert.Contains(Enumerable.Range(0, 300).Select(seed => Poe2Items.RollGear(Gear, 5, new Random(seed))), d => d?.TypeHash == Hammer.Hash);
        Assert.False(Poe2Items.IsGearBase(Keyblade));
        Assert.False(Poe2Items.IsGearBase(Wisdom));
    }

    [Fact]
    public void A_dropped_armour_takes_its_cells_in_the_inventory_when_picked_up()
    {
        var zombie = new Poe2Monster("Metadata/Monsters/Zombies/CourtGuardZombieUnarmed", 1736, 2,
            new MonsterObjectPoe2(210, 200, 1, MonsterObjectPoe2.BaseComponents));
        InstanceSessionPoe2? session = null; uint id = 0; WorldItemObjectPoe2? ground = null;
        for (int seed = 0; seed < 200 && ground is null; seed++)
        {
            session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Skills: WarriorSkills),
                monsters: [zombie], random: new Random(seed), itemTypes: new Dictionary<string, Poe2Items.ItemType> { [Cuirass.Id] = Cuirass });
            session.Handle(AreaGenerated, 0);
            session.Handle(PunchZombie, 0);
            session.Tick(1);
            foreach (var (i, g) in session.GroundItems)
                if (g.Item.TypeHash == Cuirass.Hash) (id, ground) = (i, g);
        }
        Assert.NotNull(ground);

        session!.Handle(Pick(id), 2);
        session.Tick(3);

        var entry = Assert.Single(session.MainInventory);
        Assert.Equal((Cuirass.Hash, (byte)2, (byte)3), (entry.Item.TypeHash, entry.Width, entry.Height));
        Assert.Equal(2, entry.Item.ItemLevel);
    }

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
