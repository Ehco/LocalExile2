using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>The PoE2 messages after the area: area generated, local player, enter area, create object (PORTING-POE2.md, wave 9).</summary>
public class InstanceWorldPoe2Tests
{
    [Fact]
    public void Area_generated_is_a_route_byte_then_the_tile_and_doodad_hashes()
    {
        // Decrypted C→S capture of 2026-11-03; the client logged "Tile hash: 892597717" and "Doodad hash: 979693953".
        var (tile, doodad) = InstanceProtocolPoe2.ReadAreaGenerated(Convert.FromHexString("009A003533F5D53A64F181"))!.Value;
        Assert.Equal((892597717u, 979693953u), (tile, doodad));
    }

    [Fact]
    public void Area_generated_needs_its_id_and_all_eleven_bytes()
    {
        byte[] wire = Convert.FromHexString("009A003533F5D53A64F181");
        Assert.Null(InstanceProtocolPoe2.ReadAreaGenerated(wire.AsSpan(0, 10)));
        Assert.Null(InstanceProtocolPoe2.ReadAreaGenerated(Convert.FromHexString("007D003533F5D53A64F181")));
    }

    [Fact]
    public void Local_player_carries_the_route_byte_the_slot_and_the_entity_id()
    {
        // Writer 0x141f3ce30: u8 +0x10, u8 +0x18 (the local player's slot), u32 +0x1c.
        Assert.Equal("0024" + "00" + "00" + "00001000", Convert.ToHexString(InstanceProtocolPoe2.SetLocalPlayer(0x1000)));
        Assert.Equal("0024" + "00" + "01" + "00000007", Convert.ToHexString(InstanceProtocolPoe2.SetLocalPlayer(7, slot: 1)));
    }

    [Fact]
    public void Enter_area_sends_one_empty_entry_per_local_player()
    {
        // Writer 0x142157e80: u8 route, compact u32 +0x1f8, u8 bits, u8 count + entries (two stash tab lists, u8 count of
        // u32 pairs, wstr, bytes[3]), u8 count of the second list.
        string entry = "0000" + "0000" + "00" + "0000" + "000000";
        Assert.Equal("0023" + "00" + "00" + "00" + "01" + entry + "00", Convert.ToHexString(InstanceProtocolPoe2.EnterArea()));
        Assert.Equal("0023" + "00" + "00" + "00" + "02" + entry + entry + "00", Convert.ToHexString(InstanceProtocolPoe2.EnterArea(localPlayers: 2)));
    }

    [Fact]
    public void Create_object_header_gained_a_byte_and_the_blob_length_is_compact()
    {
        // Writer 0x141f6c540: header 0x141f4b740 (u32 id, u32, u8, u16), u32 object type, compact length, blob.
        byte[] wire = InstanceProtocolPoe2.CreateObject(0x1000, 0x9c41993e, [0xaa, 0xbb]);
        Assert.Equal("02F7" + "00001000" + "00000000" + "00" + "0000" + "9C41993E" + "02" + "AABB", Convert.ToHexString(wire));
    }

    [Fact]
    public void Create_object_writes_long_blobs_with_a_two_byte_compact_length()
    {
        byte[] wire = InstanceProtocolPoe2.CreateObject(1, 2, new byte[0x80]);
        Assert.Equal("8080", Convert.ToHexString(wire.AsSpan(17, 2)));
        Assert.Equal(19 + 0x80, wire.Length);
    }
}
