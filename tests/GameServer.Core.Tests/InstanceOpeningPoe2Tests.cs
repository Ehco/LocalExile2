using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>The PoE2 client's instance opening (PORTING-POE2.md, wave 7).</summary>
public class InstanceOpeningPoe2Tests
{
    // The first bytes the PoE2 Steam client (0.5.5e) sent to 127.0.0.1:6112 after login 0x14 with ticket 1, 2026-11-03.
    private static readonly byte[] RealOpening = Convert.FromHexString("000F00000001000E12BEA056029C953E6AE9B7C87F0000010000000004");

    [Fact]
    public void Real_opening_parses_as_ticket_then_hello()
    {
        var o = InstanceOpeningPoe2.TryRead(RealOpening)!;
        Assert.Equal(1u, o.Ticket);
        Assert.Equal("12BEA056029C953E", Convert.ToHexString(o.Digest));
        Assert.Equal(0x6AE9B7C8u, o.Time); // 3 s after the login server's 0x1b time, 1793701829
        Assert.Equal((0x7F000001u, 0u), (o.Address, o.U32_28));
        Assert.Equal(4, o.Flag);
        Assert.Equal((false, false, false, false), (o.Bit4, o.Bit5, o.Bit6, o.Bit7));
    }

    [Fact]
    public void Last_byte_packs_the_flag_in_the_low_nibble_and_four_bools_above()
    {
        byte[] wire = [.. RealOpening];
        wire[^1] = 0xa7;
        var o = InstanceOpeningPoe2.TryRead(wire)!;
        Assert.Equal(7, o.Flag);
        Assert.Equal((false, true, false, true), (o.Bit4, o.Bit5, o.Bit6, o.Bit7));
    }

    [Fact]
    public void Opening_needs_all_29_bytes_and_starts_with_0x0f()
    {
        Assert.Equal(29, InstanceOpeningPoe2.Length);
        Assert.Null(InstanceOpeningPoe2.TryRead(RealOpening.AsSpan(0, 28)));
        byte[] bad = [.. RealOpening];
        bad[1] = 0x0e;
        Assert.Throws<InvalidDataException>(() => InstanceOpeningPoe2.TryRead(bad));
    }

    [Fact]
    public void Poe1_parser_does_not_accept_the_poe2_opening()
    {
        Assert.Null(InstanceProtocol.TryReadOpening(RealOpening));
    }
}
