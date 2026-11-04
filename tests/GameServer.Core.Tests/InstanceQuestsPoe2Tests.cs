using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>PoE2 quest flags on the wire (PORTING-POE2.md, wave 27).</summary>
public class InstanceQuestsPoe2Tests
{
    [Fact]
    public void A_flag_set_is_a_block_count_then_per_block_its_index_and_64_little_endian_bits()
    {
        // Reader 0x141deebf0: u16 LE count; per block u8 (flag >> 6), u64 LE bits.
        Assert.Equal("0300" + "00" + "2100000000000000" + "01" + "4000000000000000" + "FF" + "0000000000000080",
            Convert.ToHexString(InstanceQuestsPoe2.FlagSet([0, 5, 70, 16383])));
        Assert.Equal("0000", Convert.ToHexString(InstanceQuestsPoe2.FlagSet([])));
        Assert.Equal(InstanceQuestsPoe2.FlagSet([]), InstanceQuestsPoe2.FlagSet([-1, 16384]));
    }

    [Fact]
    public void Player_quest_flags_are_a_player_component_update_with_flag_bit_1()
    {
        // S→C 0x2f9: header, compact length, component 6, u16 LE flags 0x0002, the set.
        Assert.Equal("02F9" + "00001000" + "00000000" + "00" + "0000" + "0E" + "06" + "0200" + "0100" + "00" + "0100000000000000",
            Convert.ToHexString(InstanceQuestsPoe2.PlayerQuestFlags(0x1000, [0])));
    }

    [Fact]
    public void The_players_creation_blob_carries_the_flags_in_its_first_set()
    {
        byte[] none = new PlayerObjectPoe2(GridX: 1, GridY: 2, Life: 100).Blob();
        byte[] withFlags = new PlayerObjectPoe2(GridX: 1, GridY: 2, Life: 100, QuestFlags: [1, 2890]).Blob();

        Assert.Equal(PlayerObjectPoe2.BlobLength, none.Length);
        Assert.Equal(PlayerObjectPoe2.BlobLength + 2 * 9, withFlags.Length);
        // Player: u32 name count 0, u8 class, u32 experience, u32, u32, u16, u16, then the first set.
        int at = PlayerObjectPoe2.PlayerOffset + 4 + 1 + 4 + 12;
        Assert.Equal("0000" + "0000", Convert.ToHexString(none[at..(at + 4)]));
        Assert.Equal("0200" + "00" + "0200000000000000" + "2D" + "0004000000000000" + "0000",
            Convert.ToHexString(withFlags[at..(at + 2 + 18 + 2)]));
        Assert.Equal(none[(at + 4)..], withFlags[(at + 4 + 18)..]);
    }
}
