using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstanceInventoriesPoe2Tests
{
    [Fact]
    public void Add_inventory_writes_route_manager_id_and_the_inventory_object()
    {
        var main = InstanceInventoriesPoe2.PlayerDefaults[0];

        byte[] m = InstanceInventoriesPoe2.AddInventory(1, main);

        Assert.Equal("00DF" + "00" + "00" + "00000001" + "00" + "00" + "0C" + "05" + "00" + "00", Convert.ToHexString(m));
    }

    [Fact]
    public void Equipment_slots_set_the_single_item_and_equipment_flags()
    {
        var body = InstanceInventoriesPoe2.PlayerDefaults[1];

        byte[] m = InstanceInventoriesPoe2.AddInventory(InstanceInventoriesPoe2.InventoryId(body.Row), body, manager: 2);

        Assert.Equal("00DF" + "00" + "02" + "00000002" + "01" + "01" + "02" + "03" + "03" + "00", Convert.ToHexString(m));
    }

    [Fact]
    public void Player_defaults_cover_every_inventories_row_in_order()
    {
        var rows = InstanceInventoriesPoe2.PlayerDefaults;

        Assert.Equal(121, rows.Length);
        Assert.All(rows.Select((s, i) => (s, i)), x => Assert.Equal(x.i, x.s.Row));
        Assert.Equal(new InstanceInventoriesPoe2.Slot(12, 11, 4, 4, true, false), rows[InstanceInventoriesPoe2.CursorRow]);
        Assert.Equal(new InstanceInventoriesPoe2.Slot(11, 10, 5, 2, false, true), rows[11]);
    }
}
