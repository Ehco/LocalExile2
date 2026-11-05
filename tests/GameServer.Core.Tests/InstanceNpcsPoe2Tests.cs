using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>PoE2 NPC dialogue messages (PORTING-POE2.md, wave 27).</summary>
public class InstanceNpcsPoe2Tests
{
    [Fact]
    public void Talk_is_the_object_header_a_mode_and_the_player_slot()
    {
        // S→C 0xa3 (reader 0x141f6d0d0): u32 BE object id, u32, u8, u16 (the header 0x141f4b800), u8 mode, u8 slot.
        Assert.Equal("00A3" + "00001009" + "00000000" + "00" + "0000" + "FF" + "00", Convert.ToHexString(InstanceNpcsPoe2.Talk(0x1009)));
        Assert.Equal("00A3" + "00001009" + "00000000" + "00" + "0000" + "0D" + "00", Convert.ToHexString(InstanceNpcsPoe2.Talk(0x1009, 13)));
        // Object id 0 ends the dialogue.
        Assert.Equal("00A3" + "00000000" + "00000000" + "00" + "0000" + "FF" + "00", Convert.ToHexString(InstanceNpcsPoe2.Talk(0)));
    }

    [Fact]
    public void Shop_window_is_empty_until_the_shop_wave()
    {
        // S→C 0xaf (reader 0x142156ad0): u8 route, u32 BE NPC, u16 shop row, u8, u8 has tabs, u16 n lines, u16 m tabs.
        Assert.Equal("00AF" + "00" + "00001009" + "0000" + "00" + "00" + "0000" + "0000", Convert.ToHexString(InstanceNpcsPoe2.ShopWindow(0x1009)));
    }

    [Fact]
    public void Choose_talk_is_the_option_index_after_the_route_byte()
    {
        // C→S 0xa1 (4 bytes): u8 route, u8 the NPCTalk row's index among the NPC's rows.
        Assert.Equal((byte)0x15, InstanceNpcsPoe2.ReadChooseTalk(Convert.FromHexString("00A10015")));
        Assert.Null(InstanceNpcsPoe2.ReadChooseTalk(Convert.FromHexString("00A100")));
        Assert.Null(InstanceNpcsPoe2.ReadChooseTalk(Convert.FromHexString("00A40000")));
    }

    [Fact]
    public void Close_talk_is_three_bytes()
    {
        // C→S 0xa4 (3 bytes): u8 route; the dialogue window was closed or Continue pressed.
        Assert.True(InstanceNpcsPoe2.IsCloseTalk(Convert.FromHexString("00A400")));
        Assert.False(InstanceNpcsPoe2.IsCloseTalk(Convert.FromHexString("00A40000")));
        Assert.False(InstanceNpcsPoe2.IsCloseTalk(Convert.FromHexString("00A100")));
    }

    [Fact]
    public void The_dialogue_messages_are_framed_by_the_session()
    {
        var messages = InstanceSessionPoe2.Split(Convert.FromHexString("00A10015" + "00A400" + "005B00987500001009010063"), out int rest);

        Assert.Equal(["00A10015", "00A400", "005B0098750000100901"], messages.Select(m => Convert.ToHexString(m)));
        Assert.Equal(4 + 3 + 10, rest);
    }
}
