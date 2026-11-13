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
    public void Enter_area_sends_an_entry_and_a_player_record_per_local_player()
    {
        // Writer 0x142157e80: u8 route, compact u32 +0x1f8, u8 bits, u8 count + entries (two stash tab lists, u8 count of
        // u32 pairs, wstr, bytes[3]), then u8 count + player records (u32 entity id, u8 entry index | bits, u8 flags, u8,
        // three CharacterShapeshiftBasicSkills rows, u16 count of u16).
        string entry = "0000" + "0000" + "00" + "0000" + "000000";
        string Record(string id, string index) => id + index + "01" + "00" + "00" + "01" + "02" + "0000";
        Assert.Equal("0023" + "00" + "00" + "00" + "01" + entry + "01" + Record("00001000", "00"),
            Convert.ToHexString(InstanceProtocolPoe2.EnterArea(0x1000)));
        Assert.Equal("0023" + "00" + "00" + "00" + "02" + entry + entry + "02" + Record("00001000", "00") + Record("00001001", "01"),
            Convert.ToHexString(InstanceProtocolPoe2.EnterArea(0x1000, 0x1001)));
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

    [Fact]
    public void Server_frames_are_poe1s_moved_to_0x2f5_with_a_length_and_0x2f4_with_nothing()
    {
        // World-group factory 0x142186c20: 0x2f4 builds an id-only message, 0x2f5 one with a u8 at +0x10.
        Assert.Equal("02F5" + "21", Convert.ToHexString(InstanceProtocolPoe2.FrameLength(33)));
        Assert.Equal("02F4", Convert.ToHexString(InstanceProtocolPoe2.FrameEnd()));
    }
}
