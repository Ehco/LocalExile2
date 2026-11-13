using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>PoE2's inventory moves: picking up to the cursor, placing, swapping, stacking, and the inventory saved
/// (PORTING-POE2.md, wave 31; written without a live test).</summary>
public class InstanceItemsPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    const uint Main = 1, Cursor = 13;
    static readonly Poe2Items.ItemType Wisdom = new("Metadata/Items/Currency/CurrencyIdentification", 2, "Scroll of Wisdom",
        1351093167, 1, 1, 1, 2083, ["Base", "Usable", "Stack"], 40);
    static readonly Poe2Items.ItemType Transmute = new("Metadata/Items/Currency/CurrencyUpgradeToMagic", 3, "Orb of Transmutation",
        0x11223344, 1, 1, 1, 2084, ["Base", "Usable", "Stack"], 40);
    static readonly IReadOnlyDictionary<string, Poe2Items.ItemType> Types = new Dictionary<string, Poe2Items.ItemType> { [Wisdom.Id] = Wisdom, [Transmute.Id] = Transmute };

    static InstanceSessionPoe2 InWorld(IEnumerable<SavedItem>? items = null, List<IReadOnlyList<SavedItem>>? saved = null)
    {
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 58), itemTypes: Types,
            items: items, onItems: saved is null ? null : saved.Add);
        session.Handle(AreaGenerated, 0);
        return session;
    }

    static byte[] PickUp(uint inventory, uint item) =>
        Convert.FromHexString("003900" + inventory.ToString("X8") + item.ToString("X8") + "00000000" + "FF" + "00" + "00");

    static byte[] Place(uint inventory, uint x, uint y, bool one = false) =>
        Convert.FromHexString("003800" + inventory.ToString("X8") + x.ToString("X8") + y.ToString("X8") + (one ? "01" : "00") + "FF" + "00" + "00");

    static IEnumerable<string> Ids(IEnumerable<byte[]> messages) => messages.Select(m => Convert.ToHexString(m, 0, 2));

    static SavedItem Saved(uint inventory, byte x, byte y, Poe2Items.ItemType type, int stack = 1) => new(inventory, x, y, type.Hash, stack);

    [Fact]
    public void The_pick_up_and_place_requests_are_read()
    {
        Assert.Equal(new InstanceItemsPoe2.PickUp(1, 7, 0, 0, 0), InstanceItemsPoe2.ReadPickUp(PickUp(1, 7)));
        Assert.Equal(new InstanceItemsPoe2.Place(1, 3, 4, false), InstanceItemsPoe2.ReadPlace(Place(1, 3, 4)));
        Assert.Equal(new InstanceItemsPoe2.Place(1, 3, 4, true), InstanceItemsPoe2.ReadPlace(Place(1, 3, 4, one: true)));
        Assert.Null(InstanceItemsPoe2.ReadPickUp(Place(1, 3, 4)));
        Assert.Null(InstanceItemsPoe2.ReadPlace(PickUp(1, 7)));
    }

    [Fact]
    public void Saved_items_take_their_cells_again_on_entering()
    {
        var session = InWorld([Saved(Main, 3, 2, Wisdom, 5), Saved(Cursor, 0, 0, Transmute), Saved(Main, 0, 0, Wisdom with { Hash = 9 })]);

        var items = session.MainInventory;
        Assert.Equal(2, items.Count);
        Assert.Equal((3, 2, 5), (items[0].X, items[0].Y, (int)items[0].Item.StackSize));
        Assert.Equal((0, 0, Transmute.Hash), (items[1].X, items[1].Y, items[1].Item.TypeHash));   // the cursor's item, back in the inventory
        Assert.Null(session.Held);
        Assert.Equal(2, Ids(session.EnterWorld()).Count(id => id == "00DE"));
    }

    [Fact]
    public void A_click_picks_the_item_up_to_the_cursor_and_saves()
    {
        var saved = new List<IReadOnlyList<SavedItem>>();
        var session = InWorld([Saved(Main, 3, 2, Wisdom, 5)], saved);
        uint id = session.MainInventory.Single().Id;

        var reply = session.Handle(PickUp(Main, id), 0);

        Assert.Equal(["00DE", "00DE"], Ids(reply));
        Assert.Empty(session.MainInventory);
        Assert.Equal((Wisdom.Hash, 5), (session.Held!.Item.TypeHash, (int)session.Held.Item.StackSize));
        Assert.Equal([new SavedItem(Cursor, 0, 0, Wisdom.Hash, 5)], saved[^1]);
        Assert.Empty(session.Handle(PickUp(Main, id), 0));          // gone
        Assert.Empty(session.Handle(PickUp(Cursor, session.Held.Id), 0));   // not from the cursor
    }

    [Fact]
    public void The_held_item_goes_to_a_free_cell()
    {
        var session = InWorld([Saved(Main, 3, 2, Wisdom, 5)]);
        session.Handle(PickUp(Main, session.MainInventory.Single().Id), 0);

        var reply = session.Handle(Place(Main, 11, 4), 0);

        Assert.Equal(["00DE", "00DE"], Ids(reply));
        Assert.Null(session.Held);
        var placed = Assert.Single(session.MainInventory);
        Assert.Equal((11, 4, 5), (placed.X, placed.Y, (int)placed.Item.StackSize));
        Assert.Empty(session.Handle(Place(Main, 0, 0), 0));   // nothing held
    }

    [Fact]
    public void Placing_onto_another_item_swaps_them()
    {
        var session = InWorld([Saved(Main, 0, 0, Wisdom, 5), Saved(Main, 4, 0, Transmute, 2)]);
        session.Handle(PickUp(Main, session.MainInventory[0].Id), 0);

        var reply = session.Handle(Place(Main, 4, 0), 0);

        Assert.Equal(["00DE", "00DE", "00DE"], Ids(reply));
        var placed = Assert.Single(session.MainInventory);
        Assert.Equal((4, 0, Wisdom.Hash), (placed.X, placed.Y, placed.Item.TypeHash));
        Assert.Equal((Transmute.Hash, 2), (session.Held!.Item.TypeHash, (int)session.Held.Item.StackSize));
    }

    [Fact]
    public void Placing_onto_a_stack_of_its_type_merges_and_keeps_the_rest()
    {
        var session = InWorld([Saved(Main, 0, 0, Wisdom, 30), Saved(Main, 4, 0, Wisdom, 38)]);
        session.Handle(PickUp(Main, session.MainInventory[0].Id), 0);

        session.Handle(Place(Main, 4, 0), 0);

        var stack = Assert.Single(session.MainInventory);
        Assert.Equal(40, stack.Item.StackSize);
        Assert.Equal(28, session.Held!.Item.StackSize);
    }

    [Fact]
    public void With_shift_one_of_the_held_stack_is_placed()
    {
        var session = InWorld([Saved(Main, 0, 0, Wisdom, 5)]);
        session.Handle(PickUp(Main, session.MainInventory[0].Id), 0);

        session.Handle(Place(Main, 2, 2, one: true), 0);

        Assert.Equal(1, Assert.Single(session.MainInventory).Item.StackSize);
        Assert.Equal(4, session.Held!.Item.StackSize);
    }

    [Fact]
    public void A_cell_outside_the_grid_or_over_two_items_is_refused()
    {
        var session = InWorld([Saved(Main, 0, 0, Wisdom, 5), Saved(Main, 1, 0, Transmute), Saved(Main, 2, 0, Transmute)]);
        session.Handle(PickUp(Main, session.MainInventory[0].Id), 0);

        Assert.Empty(session.Handle(Place(Main, 12, 0), 0));
        Assert.Empty(session.Handle(Place(Main, 0, 5), 0));
        Assert.Empty(session.Handle(Place(Cursor, 0, 0), 0));
        Assert.NotNull(session.Held);
        Assert.Equal(2, session.MainInventory.Count);
    }

    [Fact]
    public void The_inventory_model_refuses_a_placement_over_two_items()
    {
        var inventory = new Poe2Inventory(1, 4, 4);
        inventory.Add(Poe2Items.Create(Wisdom), 1, 1, 40);
        inventory.Add(Poe2Items.Create(Transmute), 1, 1, 40);   // at (1, 0)

        Assert.Null(inventory.Place(Poe2Items.Create(Transmute), 2, 1, 0, 0, 40));
        Assert.NotNull(inventory.Place(Poe2Items.Create(Transmute), 2, 1, 2, 0, 40));
        Assert.Equal(3, inventory.Items.Count);
        Assert.Null(inventory.Restore(Poe2Items.Create(Wisdom), 1, 1, 0, 0));
        Assert.NotNull(inventory.Remove(inventory.Items[0].Id));
        Assert.Null(inventory.Remove(999));
    }
}
