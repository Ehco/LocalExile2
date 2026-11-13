using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

/// <summary>PoE2 direction movement, C→S 0x63 and S→C 0x64 (PORTING-POE2.md, wave 12).</summary>
public class InstanceMovementPoe2Tests
{
    [Theory]
    // Captured live 2026-11-03 (lockstep, so the cell is 0, 0): clicks down-right, up-left, straight up, then release.
    [InlineData("006300807F807F000000", 127, 127)]
    [InlineData("006300BF81BF81000000", -127, -127)]
    [InlineData("00630000BF4C000000", 0, -180)]
    [InlineData("0063000000000000", 0, 0)]
    public void Direction_move_is_the_slot_four_compact_ints_and_a_flag(string hex, int dx, int dy)
    {
        byte[] wire = Convert.FromHexString(hex);

        var move = InstanceMovementPoe2.ReadDirectionMove(wire, out int length);

        Assert.Equal(new InstanceMovementPoe2.DirectionMove(0, dx, dy, 0, 0, 0), move);
        Assert.Equal(wire.Length, length);
        Assert.Equal(dx == 0 && dy == 0, move!.IsStop);
    }

    [Fact]
    public void Direction_move_needs_its_id_and_every_field()
    {
        byte[] wire = Convert.FromHexString("006300807F807F000000");

        Assert.Null(InstanceMovementPoe2.ReadDirectionMove(wire.AsSpan(0, 9), out _));
        Assert.Null(InstanceMovementPoe2.ReadDirectionMove(Convert.FromHexString("006400807F807F000000"), out _));
    }

    [Fact]
    public void Echo_is_the_object_header_then_the_direction_and_the_cell()
    {
        byte[] wire = InstanceMovementPoe2.DirectionMoveEcho(0x1000, 127, -127, 200, 200);

        Assert.Equal("0064" + "00001000" + "00000000" + "00" + "0000" + "807F" + "BF81" + "80C8" + "80C8" + "00",
            Convert.ToHexString(wire));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(63)]
    [InlineData(-64)]
    [InlineData(64)]
    [InlineData(-8192)]
    [InlineData(0x1fff)]
    [InlineData(-0x100000)]
    [InlineData(0x3ffffff)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Compact_int_reads_back_what_the_writer_wrote(int value)
    {
        var w = new LoginWriter();
        InstanceProtocol.CompactInt(w, value);
        byte[] bytes = w.ToArray();
        int at = 0;

        Assert.True(InstanceMovementPoe2.TryReadCompactInt(bytes, ref at, out int read));
        Assert.Equal(value, read);
        Assert.Equal(bytes.Length, at);
    }
}
