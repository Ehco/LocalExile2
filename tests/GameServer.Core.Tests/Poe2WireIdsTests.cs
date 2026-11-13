using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class Poe2WireIdsTests
{
    private static readonly Poe2WireIds Shifted = Poe2WireIds.Parse("9b:1, 2f0:3");

    [Theory]
    [InlineData(0x24, 0x24)]      // below the first inserted id
    [InlineData(0x9a, 0x9a)]
    [InlineData(0x9b, 0x9c)]
    [InlineData(0x2ef, 0x2f0)]
    [InlineData(0x2f0, 0x2f3)]
    [InlineData(0x2f7, 0x2fa)]
    public void An_id_moves_up_by_the_shift_of_its_range_and_comes_back(int id, int wire)
    {
        Assert.Equal((ushort)wire, Shifted.ToWire((ushort)id));
        Assert.True(Shifted.TryFromWire((ushort)wire, out ushort back));
        Assert.Equal((ushort)id, back);
    }

    [Theory]
    [InlineData(0x9b)]            // the id inserted before 0x9b
    [InlineData(0x2f1)]           // the two inserted before 0x2f0
    [InlineData(0x2f2)]
    public void An_id_the_update_added_has_no_number_in_the_code(int wire) =>
        Assert.False(Shifted.TryFromWire((ushort)wire, out _));

    [Theory]
    [InlineData(0x29, 0x29)]      // connect to instance
    [InlineData(0x2db, 0x2db)]    // gold
    [InlineData(0x2e8, 0x2e8)]    // the client still sends it under this id
    [InlineData(0x2f1, 0x2f4)]    // change area: the client took our frame end 0x2f4 for one
    [InlineData(0x2f4, 0x2f7)]    // frame end
    [InlineData(0x2f5, 0x2f8)]    // frame length
    [InlineData(0x2f7, 0x2fa)]    // create object
    [InlineData(0x324, 0x327)]    // weapon records
    [InlineData(0x355, 0x358)]    // the client's leaving
    [InlineData(0x357, 0x35a)]
    public void Client_055f_has_everything_from_the_world_group_up_three_ids_higher(int id, int wire)
    {
        Assert.Equal((ushort)wire, Poe2WireIds.Client055f.ToWire((ushort)id));
        Assert.True(Poe2WireIds.Client055f.TryFromWire((ushort)wire, out ushort back));
        Assert.Equal((ushort)id, back);
    }

    [Fact]
    public void Identity_changes_nothing()
    {
        Assert.Equal(0x2f7, Poe2WireIds.Identity.ToWire(0x2f7));
        Assert.True(Poe2WireIds.Identity.TryFromWire(0x2f7, out ushort id));
        Assert.Equal(0x2f7, id);
        Assert.Equal([0x02, 0xf7, 0x01], Poe2WireIds.Identity.ToWire([0x02, 0xf7, 0x01]));
    }

    [Fact]
    public void A_buffer_gets_its_id_rewritten_in_a_copy()
    {
        byte[] message = [0x02, 0xf7, 0x00, 0x9b];

        byte[] wire = Shifted.ToWire(message);

        Assert.Equal([0x02, 0xfa, 0x00, 0x9b], wire);
        Assert.Equal([0x02, 0xf7, 0x00, 0x9b], message);
    }

    [Fact]
    public void A_buffer_of_equal_messages_gets_every_id_rewritten()
    {
        byte[] three = [.. Enumerable.Repeat(InstanceInventoriesPoe2.AddInventory(1, InstanceInventoriesPoe2.PlayerDefaults[0]), 3).SelectMany(m => m)];
        Assert.Equal(3 * InstanceInventoriesPoe2.AddInventoryLength, three.Length);

        byte[] wire = Shifted.ToWire(three, InstanceInventoriesPoe2.AddInventoryLength);

        ushort expected = Shifted.ToWire(InstanceInventoriesPoe2.AddInventoryId);
        for (int at = 0; at < wire.Length; at += InstanceInventoriesPoe2.AddInventoryLength)
            Assert.Equal(expected, (ushort)(wire[at] << 8 | wire[at + 1]));
    }

    [Fact]
    public void Shifts_that_shrink_with_the_id_are_refused() =>
        Assert.Throws<ArgumentException>(() => Poe2WireIds.Parse("9b:2,2f0:1"));

    [Fact]
    public void Split_gives_back_the_codes_ids_for_a_shifted_client()
    {
        // C→S 0x1ff (pause request, 4 bytes) as a client three ids up sends it.
        var ids = Poe2WireIds.Parse("1f0:3");
        byte[] fromClient = [0x02, 0x02, 0x00, 0x01];

        var messages = InstanceSessionPoe2.Split(fromClient, out int rest, ids);

        Assert.Equal(4, rest);
        Assert.Equal([0x01, 0xff, 0x00, 0x01], Assert.Single(messages));
        Assert.Empty(InstanceSessionPoe2.Split(fromClient, out _));   // 0x202 means nothing in the code's numbering
    }
}
