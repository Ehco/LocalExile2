using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>PoE2's passive skill tree: the tree message, the tree screen's requests, the session's allocations and
/// refunds (paid in gold) and the nodes' stats (PORTING-POE2.md, wave 28).</summary>
public class InstancePassivesPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    const uint Player = InstanceSessionPoe2.DefaultPlayerId;
    const ushort MeleeDamage = 0x0f60, Attribute = 0x5c12, Strength = 0x66b9, Dexterity = 0x3a4f, Other = 0x1239;
    const uint MeleeDamageKey = 100;
    const int RefundCost = 15;   // GoldRespecPrices at level 1
    static readonly Poe2Classes.Row Warrior = Poe2Classes.Of(CharacterClass.Warrior);

    // Melee Damage (a stat sent as it is), an Attribute node (+5 of the player's choice, no stat of its own) and the
    // two generic nodes it may choose; refunds cost 15 gold at level 1, 19 at level 2.
    static readonly Poe2Passives Nodes = new(new Dictionary<ushort, Poe2Passives.Node>
    {
        [MeleeDamage] = new(MeleeDamage, "melee_damage1", "Melee Damage", [("melee_damage_+%", MeleeDamageKey, 10)]),
        [Attribute] = new(Attribute, "strength26", "Attribute", [], Attribute: true),
        [Strength] = new(Strength, "generic_attribute_strength", "", [("base_strength", Poe2CharacterStats.Strength, 5)]),
        [Dexterity] = new(Dexterity, "generic_attribute_dexterity", "", [("base_dexterity", Poe2CharacterStats.Dexterity, 5)]),
    }, respecPrices: [RefundCost, 19]);

    static byte[] Allocate(ushort node, ushort choice = 0) =>
        new LoginWriter().U16(InstancePassivesPoe2.AllocateId).U8(0).U16(node).U8(0).U16(choice).U8(0).ToArray();

    static byte[] Refund(ushort node) => new LoginWriter().U16(InstancePassivesPoe2.RefundId).U8(0).U16(node).U8(0).ToArray();

    static readonly byte[] RefundAll = Convert.FromHexString("004800");

    // A level 1 Warrior in the world with the quest points and gold given, saving its allocations into saved.
    static InstanceSessionPoe2 InWorld(ushort questPoints, List<IReadOnlyCollection<int>>? saved = null, IEnumerable<int>? passives = null,
        uint gold = 0)
    {
        var player = new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Mana: 48, ClassRow: 6);
        var session = new InstanceSessionPoe2(player, passives: passives, onPassives: saved is null ? null : saved.Add,
            questPassivePoints: questPoints, passiveNodes: Nodes, gold: gold);
        session.Handle(AreaGenerated, 0);
        return session;
    }

    static byte[] Tree(ushort questPoints, IEnumerable<(ushort Node, ushort Choice)> allocations) =>
        InstancePassivesPoe2.CharacterPassives(1, 6, allocations.Select(a => a.Node), allocations, questPoints: questPoints);

    static byte[] Stats(params (ushort Node, ushort Choice)[] allocations) =>
        InstanceCombatPoe2.StatsUpdate(Player, Poe2CharacterStats.For(Warrior, 1, Nodes.Modifiers(allocations)).Stats);

    static byte[] Gold(uint amount) => InstancePassivesPoe2.Gold(amount);

    [Fact]
    public void The_tree_lists_the_nodes_sorted_and_the_attribute_choices_last()
    {
        byte[] m = InstancePassivesPoe2.CharacterPassives(5, 6, [Other, MeleeDamage, Attribute], [(Attribute, Strength), (MeleeDamage, 0)],
            questPoints: 10);

        Assert.Equal("003A00" + "000A" + "0000" + "0000" + "05" + "06" + "0000" + "00"
            + "0003" + "0F6000" + "123900" + "5C1200" + "0000" + "0000" + "0000"
            + "01" + "125CB966", Convert.ToHexString(m));
    }

    [Fact]
    public void An_empty_tree_has_the_level_the_class_and_empty_lists()
    {
        Assert.Equal("003A00" + "0000" + "0000" + "0000" + "01" + "00" + "0000" + "00" + "0000" + "0000" + "0000" + "0000" + "00",
            Convert.ToHexString(InstancePassivesPoe2.CharacterPassives(1, 0)));
    }

    [Fact]
    public void The_gold_is_a_big_endian_amount()
    {
        Assert.Equal("02DB00" + "00000064", Convert.ToHexString(Gold(100)));
    }

    [Fact]
    public void The_tree_screens_requests_are_read()
    {
        // Live 2026-11-04: Attribute 13397 chosen as Strength, Melee Damage 3936.
        Assert.Equal(new InstancePassivesPoe2.Allocate(0x3455, 0, Strength, 0),
            InstancePassivesPoe2.ReadAllocate(Convert.FromHexString("003C0034550066B900")));
        Assert.Equal(new InstancePassivesPoe2.Allocate(MeleeDamage, 0, 0, 0), InstancePassivesPoe2.ReadAllocate(Allocate(MeleeDamage)));
        Assert.Null(InstancePassivesPoe2.ReadAllocate(Refund(MeleeDamage)));
        Assert.Equal((MeleeDamage, (byte)0), InstancePassivesPoe2.ReadRefund(Refund(MeleeDamage)));
        Assert.Null(InstancePassivesPoe2.ReadRefund(Allocate(MeleeDamage)));
        Assert.True(InstancePassivesPoe2.IsRefundAll(RefundAll));
        Assert.False(InstancePassivesPoe2.IsRefundAll(Convert.FromHexString("004700")));
    }

    [Fact]
    public void An_allocation_takes_a_point_and_sends_the_tree_and_the_stats()
    {
        var saved = new List<IReadOnlyCollection<int>>();
        var session = InWorld(2, saved);

        var reply = session.Handle(Allocate(MeleeDamage), 0);

        Assert.Equal([Tree(2, [(MeleeDamage, 0)]), Stats((MeleeDamage, 0))], reply);
        Assert.Contains((MeleeDamageKey, 10), session.Sheet.Stats);
        Assert.Equal(1, session.PassivePointsLeft);
        Assert.Equal([(int)MeleeDamage], Assert.Single(saved));
    }

    [Fact]
    public void An_attribute_node_grants_the_chosen_attribute()
    {
        var session = InWorld(2);
        session.Handle(Allocate(MeleeDamage), 0);

        var reply = session.Handle(Allocate(Attribute, Strength), 0);

        Assert.Equal([Tree(2, [(MeleeDamage, 0), (Attribute, Strength)]), Stats((MeleeDamage, 0), (Attribute, Strength))], reply);
        Assert.Equal(20, session.Sheet.Strength);
        Assert.Equal(68, session.Sheet.MaximumLife);
        Assert.Equal(0, session.PassivePointsLeft);
        Assert.Equal([MeleeDamage, Poe2Passives.Encode(Attribute, Strength)], session.Passives);
    }

    [Fact]
    public void Without_a_point_left_the_tree_goes_back_unchanged()
    {
        var session = InWorld(1);
        session.Handle(Allocate(MeleeDamage), 0);

        var reply = session.Handle(Allocate(Other), 0);

        Assert.Equal([Tree(1, [(MeleeDamage, 0)])], reply);
        Assert.Equal([(int)MeleeDamage], session.Passives);
    }

    [Fact]
    public void A_node_asked_for_again_costs_nothing_and_takes_the_new_choice()
    {
        var session = InWorld(1);
        session.Handle(Allocate(Attribute, Strength), 0);

        session.Handle(Allocate(Attribute, Dexterity), 0);

        Assert.Equal(0, session.PassivePointsLeft);
        Assert.Equal([Poe2Passives.Encode(Attribute, Dexterity)], session.Passives);
        Assert.Equal((15, 12), (session.Sheet.Strength, session.Sheet.Dexterity));
    }

    [Fact]
    public void A_refund_costs_the_levels_gold_and_gives_the_point_back()
    {
        var saved = new List<IReadOnlyCollection<int>>();
        var session = InWorld(2, saved, gold: 100);
        session.Handle(Allocate(MeleeDamage), 0);
        session.Handle(Allocate(Attribute, Strength), 0);

        var reply = session.Handle(Refund(Attribute), 0);

        Assert.Equal([Tree(2, [(MeleeDamage, 0)]), Stats((MeleeDamage, 0)), Gold(85)], reply);
        Assert.Equal((1, 85u, RefundCost), (session.PassivePointsLeft, session.Gold, session.RefundCost));
        Assert.Equal(15, session.Sheet.Strength);
        Assert.Equal([(int)MeleeDamage], saved[^1]);
    }

    [Fact]
    public void A_refund_reports_the_gold_to_save()
    {
        var progress = new List<(uint Experience, uint Gold)>();
        var player = new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58, Mana: 48, ClassRow: 6);
        var session = new InstanceSessionPoe2(player, questPassivePoints: 2, passiveNodes: Nodes, gold: 100,
            onProgress: (e, g) => progress.Add((e, g)));
        session.Handle(AreaGenerated, 0);
        session.Handle(Allocate(MeleeDamage), 0);

        session.Handle(Refund(MeleeDamage), 0);

        Assert.Equal([(0u, 85u)], progress);
    }

    [Fact]
    public void A_refund_of_a_node_not_allocated_changes_nothing()
    {
        var session = InWorld(2, gold: 100);
        session.Handle(Allocate(MeleeDamage), 0);

        Assert.Equal([Tree(2, [(MeleeDamage, 0)])], session.Handle(Refund(Other), 0));
        Assert.Equal(100u, session.Gold);
    }

    [Fact]
    public void Short_of_gold_the_refund_is_refused()
    {
        var session = InWorld(2, gold: RefundCost - 1);
        session.Handle(Allocate(MeleeDamage), 0);

        var reply = session.Handle(Refund(MeleeDamage), 0);

        Assert.Equal([Tree(2, [(MeleeDamage, 0)]), Gold(RefundCost - 1)], reply);
        Assert.Equal([(int)MeleeDamage], session.Passives);
    }

    [Fact]
    public void Refund_all_clears_the_tree_at_the_price_of_every_node()
    {
        var saved = new List<IReadOnlyCollection<int>>();
        var session = InWorld(2, saved, gold: 100);
        session.Handle(Allocate(MeleeDamage), 0);
        session.Handle(Allocate(Attribute, Strength), 0);

        var reply = session.Handle(RefundAll, 0);

        Assert.Equal([Tree(2, []), Stats(), Gold(70)], reply);
        Assert.Empty(session.Passives);
        Assert.Empty(saved[^1]);
        Assert.Equal(2, session.PassivePointsLeft);
        Assert.Equal([Tree(2, [])], session.Handle(RefundAll, 0));
    }

    [Fact]
    public void Refund_all_short_of_gold_is_refused_whole()
    {
        var session = InWorld(2, gold: 2 * RefundCost - 1);
        session.Handle(Allocate(MeleeDamage), 0);
        session.Handle(Allocate(Attribute, Strength), 0);

        var reply = session.Handle(RefundAll, 0);

        Assert.Equal([Tree(2, [(MeleeDamage, 0), (Attribute, Strength)]), Gold(2 * RefundCost - 1)], reply);
        Assert.Equal(2, session.Passives.Count);
    }

    [Fact]
    public void Saved_allocations_and_the_gold_count_on_entering()
    {
        var session = InWorld(1, passives: [Poe2Passives.Encode(Attribute, Strength)], gold: 40);

        Assert.Equal(20, session.Sheet.Strength);
        Assert.Equal(0, session.PassivePointsLeft);
        var entering = session.EnterWorld();
        Assert.Contains(Tree(1, [(Attribute, Strength)]), entering);
        Assert.Contains(Gold(40), entering);
    }

    [Fact]
    public void The_points_come_from_the_level_and_the_quests()
    {
        Assert.Equal(1, InstanceSessionPoe2.PointsPerLevel);
        Assert.Equal(3, InWorld(3).PassivePointsLeft);
        Assert.Equal(0, InWorld(0).PassivePointsLeft);
    }

    [Fact]
    public void An_allocation_encodes_the_node_and_its_choice()
    {
        Assert.Equal(0x66b95c12, Poe2Passives.Encode(Attribute, Strength));
        Assert.Equal((Attribute, Strength), Poe2Passives.Decode(0x66b95c12));
        Assert.Equal((MeleeDamage, (ushort)0), Poe2Passives.Decode(MeleeDamage));
    }

    [Fact]
    public void The_modifiers_are_the_nodes_stats_or_the_chosen_nodes()
    {
        var modifiers = Nodes.Modifiers([(MeleeDamage, 0), (Attribute, Strength), (Other, 0)]).ToList();

        Assert.Equal([("melee_damage_+%", MeleeDamageKey, 10), ("base_strength", Poe2CharacterStats.Strength, 5)], modifiers);
    }

    [Fact]
    public void The_refund_cost_follows_the_level_and_is_free_without_the_table()
    {
        Assert.Equal((15, 15, 19, 19), (Nodes.RefundCost(0), Nodes.RefundCost(1), Nodes.RefundCost(2), Nodes.RefundCost(90)));
        Assert.Equal(0, Poe2Passives.Empty.RefundCost(1));
    }

    [Fact]
    public void The_tables_load_from_their_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"passives-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string nodes = Path.Combine(dir, "passives.json"), prices = Path.Combine(dir, "prices.json");
        File.WriteAllText(nodes, """
            [
             {"graphId":3936,"id":"melee_damage1","name":"Melee Damage","stats":[["melee_damage_+%",100,10]],"points":0},
             {"graphId":23570,"id":"strength26","name":"Attribute","stats":[],"points":0,"attribute":true},
             {"graphId":100,"id":"keystone1","name":"Resolute Technique","stats":[["never_crit",50,1],["hits_cant_be_evaded",51,1]],"points":0,"keystone":true,"notable":true}
            ]
            """);
        File.WriteAllText(prices, "[[2,19],[1,15],[3,25]]");
        try
        {
            var table = Poe2Passives.Load(nodes, prices);

            Assert.Equal(3, table.Count);
            Assert.Equal([("melee_damage_+%", 100u, 10)], table.Find(3936)!.Stats);
            Assert.True(table.Find(23570)!.Attribute);
            Assert.Equal((true, true, 2), (table.Find(100)!.Keystone, table.Find(100)!.Notable, table.Find(100)!.Stats.Count));
            Assert.Null(table.Find(1));
            Assert.Equal((15, 19, 25, 25), (table.RefundCost(1), table.RefundCost(2), table.RefundCost(3), table.RefundCost(4)));
            Assert.Equal((0, 15), (Poe2Passives.Load(Path.Combine(dir, "none.json"), prices).Count, Poe2Passives.Load(Path.Combine(dir, "none.json"), prices).RefundCost(1)));
            Assert.Equal(0, Poe2Passives.Load(nodes, Path.Combine(dir, "none.json")).RefundCost(1));
        }
        finally { Directory.Delete(dir, true); }
        Assert.Equal(0, Poe2Passives.Load(nodes, prices).Count);
    }

    [Fact]
    public void The_sheet_folds_the_attribute_life_and_mana_modifiers_and_keeps_the_rest()
    {
        var sheet = Poe2CharacterStats.For(Warrior, 1,
        [
            ("base_strength", Poe2CharacterStats.Strength, 5), ("additional_all_attributes", 0, 3),
            ("maximum_life_+%", 0, 10), ("base_maximum_mana", 0, 20), ("maximum_mana_+%", 0, 50),
            ("melee_damage_+%", MeleeDamageKey, 10), ("melee_damage_+%", MeleeDamageKey, 5), ("display_passive_attribute_text", 7, 1),
        ]);

        // Warrior 15 / 7 / 7: strength 23, dexterity and intelligence 10; life (16 + 12 + 46) × 1.1; mana (30 + 4 + 20 + 20) × 1.5.
        Assert.Equal((23, 10, 10), (sheet.Strength, sheet.Dexterity, sheet.Intelligence));
        Assert.Equal((81, 111), (sheet.MaximumLife, sheet.MaximumMana));
        Assert.Equal([(MeleeDamageKey, 15)], sheet.Extra);
        Assert.Equal(6, sheet.Stats.Count);
    }

    [Fact]
    public void Without_modifiers_the_sheet_is_the_base_one()
    {
        Assert.Equal(Poe2CharacterStats.For(Warrior, 3), Poe2CharacterStats.For(Warrior, 3, []));
        Assert.Null(Poe2CharacterStats.For(Warrior, 3, []).Extra);
    }
}
